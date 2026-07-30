using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.RoundTrip;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Api.Controllers;

/// <summary>
/// Rack-scoped desired-state YAML round-trip endpoints (story #169, Tasks #183/#184/#187). The server owns
/// all YAML work — the UI is a thin client that composes parse→edit→render. Mirrors
/// <see cref="NetworkIntentController"/> exactly: derives from <see cref="DiscoveryControllerBase"/> (these
/// are policy-gated non-GET actions), checks per-rack access/existence first, gates both actions behind the
/// elevated <see cref="AuthorizationPolicies.NetworkConfigAuthor"/> permission, caps the request body at
/// <see cref="DesiredStateSchema.MaxYamlDocumentBytes"/>, returns RFC7807 ProblemDetails carrying path +
/// line/column, and audits every operation with counts/warning codes ONLY — never the YAML body.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/desired-state")]
[Produces("application/json")]
public sealed class DesiredStateRoundTripController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly DesiredStateRoundTripMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<DesiredStateRoundTripController> _logger;

    public DesiredStateRoundTripController(
        CaissonDbContext context,
        IAuditEventWriter audit,
        ICorrelationContext correlation,
        DesiredStateRoundTripMetrics metrics,
        TimeProvider time,
        ILogger<DesiredStateRoundTripController> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Parses a YAML document into the UI-supported model plus byte-for-byte preserved unknown blocks and
    /// warnings (AC2/AC3/AC4). On any syntax/schema/semantic error returns 400 ProblemDetails with the failing
    /// paths and line/column, and NO partial model.
    /// </summary>
    [HttpPost("parse")]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [EnableRateLimiting(RateLimitPolicies.NetworkConfigRoundTrip)]
    [RequestSizeLimit(DesiredStateSchema.MaxYamlDocumentBytes)]
    [ProducesResponseType(typeof(DesiredStateRoundTripEnvelopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateRoundTripEnvelopeDto>> Parse(
        Guid rackId, [FromBody] DesiredStateParseRequest? request, CancellationToken cancellationToken)
    {
        if (request?.Yaml is null)
        {
            return ValidationError((nameof(request.Yaml), "A YAML document is required."));
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var actorId = ResolveActorId();
        using var scope = _logger.BeginScope(LogScope(rackId, actorId, "import"));

        var stopwatch = Stopwatch.StartNew();
        var result = DesiredStateYamlImporter.Import(request.Yaml);
        stopwatch.Stop();

        var outcome = result.IsSuccess
            ? DesiredStateRoundTripOutcome.Success
            : DesiredStateRoundTripOutcome.Invalid;
        _metrics.RecordParse(outcome, stopwatch.Elapsed);

        var warningCodes = result.IsSuccess
            ? result.Envelope!.Warnings.Select(DesiredStateRoundTripContractMappers.ToWarningCode).ToList()
            : new List<string>();

        await WriteAuditAsync(
            rackId, "desired-state.parsed", actorId, outcome,
            vlanCount: result.Envelope?.SupportedModel.VlanCatalogue.Count ?? 0,
            portIntentCount: result.Envelope?.SupportedModel.PortIntents.Count ?? 0,
            unknownBlockCount: result.Envelope?.UnknownBlocks.Count ?? 0,
            issueCount: result.Issues.Count,
            warningCodes: warningCodes,
            cancellationToken);

        _logger.LogInformation(
            "Desired-state parse {Outcome} in {DurationMs}ms ({IssueCount} issues, {WarningCount} warnings).",
            outcome, stopwatch.Elapsed.TotalMilliseconds, result.Issues.Count, warningCodes.Count);

        if (!result.IsSuccess)
        {
            return ImportIssues(result.Issues);
        }

        return Ok(DesiredStateRoundTripContractMappers.ToDto(result.Envelope!));
    }

    /// <summary>
    /// Deterministically renders the supported model (plus any preserved unknown blocks) to canonical YAML
    /// (AC1/AC2). Resolves <c>metadata.rackSlug</c> from the rack itself, re-validates via the shared
    /// validator, and returns 400 on any validation/checksum failure. The response is UTF-8, LF-only YAML.
    /// </summary>
    [HttpPost("render")]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [EnableRateLimiting(RateLimitPolicies.NetworkConfigRoundTrip)]
    [RequestSizeLimit(DesiredStateSchema.MaxYamlDocumentBytes)]
    [ProducesResponseType(typeof(DesiredStateRenderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateRenderResponse>> Render(
        Guid rackId, [FromBody] DesiredStateRenderRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationError((nameof(request), "A request body is required."));
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var rackSlug = await _context.Racks
            .Where(r => r.Id == rackId)
            .Select(r => r.ExternalKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (rackSlug is null)
        {
            return RackNotFound(rackId);
        }

        var actorId = ResolveActorId();
        using var scope = _logger.BeginScope(LogScope(rackId, actorId, "export"));

        var (vlanCatalogue, portIntents) = DesiredStateRoundTripContractMappers.FromRequest(request);
        var unknownBlocks = DesiredStateRoundTripContractMappers.FromRequest(request.UnknownBlocks);
        var warnings = DesiredStateRoundTripContractMappers.WarningsFromRequest(request.Warnings);
        var model = new SupportedDesiredStateModel(rackSlug, vlanCatalogue, portIntents);

        var stopwatch = Stopwatch.StartNew();
        DesiredStateRenderResult rendered;
        try
        {
            rendered = DesiredStateYamlRenderer.Render(model, unknownBlocks, warnings);
        }
        catch (DesiredStateRenderException ex)
        {
            stopwatch.Stop();
            _metrics.RecordRender(DesiredStateRoundTripOutcome.Invalid, stopwatch.Elapsed);
            await WriteAuditAsync(
                rackId, "desired-state.rendered", actorId, DesiredStateRoundTripOutcome.Invalid,
                vlanCount: vlanCatalogue.Count, portIntentCount: portIntents.Count,
                unknownBlockCount: unknownBlocks.Count, issueCount: ex.Errors.Count,
                warningCodes: new List<string>(), cancellationToken);
            _logger.LogInformation(
                "Desired-state render invalid in {DurationMs}ms ({IssueCount} issues).",
                stopwatch.Elapsed.TotalMilliseconds, ex.Errors.Count);
            return RenderErrors(ex.Errors);
        }

        stopwatch.Stop();
        _metrics.RecordRender(DesiredStateRoundTripOutcome.Success, stopwatch.Elapsed);

        var warningCodes = rendered.Warnings.Select(DesiredStateRoundTripContractMappers.ToWarningCode).ToList();
        await WriteAuditAsync(
            rackId, "desired-state.rendered", actorId, DesiredStateRoundTripOutcome.Success,
            vlanCount: vlanCatalogue.Count, portIntentCount: portIntents.Count,
            unknownBlockCount: unknownBlocks.Count, issueCount: 0,
            warningCodes: warningCodes, cancellationToken);

        _logger.LogInformation(
            "Desired-state render success in {DurationMs}ms ({WarningCount} warnings).",
            stopwatch.Elapsed.TotalMilliseconds, warningCodes.Count);

        return Ok(new DesiredStateRenderResponse(rendered.Yaml, warningCodes));
    }

    /// <summary>Maps accumulated import issues onto an RFC7807 ValidationProblem carrying path + line/column.</summary>
    private ActionResult ImportIssues(IReadOnlyList<DesiredStateImportIssue> issues)
    {
        foreach (var issue in issues)
        {
            ModelState.AddModelError(issue.Path, MessageWithPosition(issue));
        }

        var problem = new ValidationProblemDetails(ModelState);
        problem.Extensions["issues"] = issues
            .Select(i => new DesiredStateImportIssueDto(i.Path, i.Message, i.Line, i.Column))
            .ToList();
        return ValidationProblem(problem);
    }

    /// <summary>Maps renderer/validator field errors onto an RFC7807 ValidationProblem.</summary>
    private ActionResult RenderErrors(IReadOnlyList<(string Field, string Message)> errors)
    {
        foreach (var (field, message) in errors)
        {
            ModelState.AddModelError(field, message);
        }

        return ValidationProblem(ModelState);
    }

    private static string MessageWithPosition(DesiredStateImportIssue issue)
        => issue is { Line: { } line, Column: { } column }
            ? $"{issue.Message} (line {line}, column {column})"
            : issue.Message;

    private async Task WriteAuditAsync(
        Guid rackId, string action, string actorId, DesiredStateRoundTripOutcome outcome,
        int vlanCount, int portIntentCount, int unknownBlockCount, int issueCount,
        IReadOnlyList<string> warningCodes, CancellationToken cancellationToken)
    {
        // Counts + warning codes ONLY — never the YAML body or descriptions (NFR4, story AC).
        var detailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.NetworkConfigAuthor,
            ["correlationId"] = _correlation.CorrelationId,
            ["outcome"] = outcome.ToString().ToLowerInvariant(),
            ["vlanCount"] = vlanCount,
            ["portIntentCount"] = portIntentCount,
            ["unknownBlockCount"] = unknownBlockCount,
            ["issueCount"] = issueCount,
            ["warnings"] = warningCodes,
        });

        await _audit.WriteActionAsync(
            User, rackId, action, "rack-desired-state", rackId.ToString(),
            outcome == DesiredStateRoundTripOutcome.Success ? "success" : "rejected",
            cancellationToken, detailsJson);
    }

    private Dictionary<string, object?> LogScope(Guid rackId, string actorId, string operation)
        => new(StringComparer.Ordinal)
        {
            ["correlationId"] = _correlation.CorrelationId,
            ["rackId"] = rackId,
            ["actorId"] = actorId,
            ["operation"] = operation,
        };

    private string ResolveActorId()
        => User.FindFirst("oid")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.Identity?.Name
            ?? "unknown";
}
