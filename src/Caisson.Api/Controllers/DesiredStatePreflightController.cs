using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Caisson.Ingestion.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Caisson.Api.Controllers;

/// <summary>
/// Pre-flight validation for network-config authoring (story #170, AC1/AC2/AC3/AC4). Runs the pure
/// schema → semantic → safety pipeline against the rack's latest observed topology and returns 200 with the
/// issues grouped by severity plus a server-issued, content-bound <c>validationRunId</c> — never a 500 or a
/// stack trace for a validation failure (NFR1). Side-effect free except the audit write (NFR3). Modelled
/// exactly on <see cref="DesiredStateRoundTripController"/>.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/desired-state")]
[Produces("application/json")]
public sealed class DesiredStatePreflightController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly PreflightValidationMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<DesiredStatePreflightController> _logger;

    public DesiredStatePreflightController(
        CaissonDbContext context,
        IBestEffortAuditEventWriter audit,
        ICorrelationContext correlation,
        PreflightValidationMetrics metrics,
        TimeProvider time,
        ILogger<DesiredStatePreflightController> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("preflight-validate")]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [EnableRateLimiting(RateLimitPolicies.NetworkConfigRoundTrip)]
    [RequestSizeLimit(DesiredStateSchema.MaxYamlDocumentBytes)]
    [ProducesResponseType(typeof(PreflightValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreflightValidationResponse>> Validate(
        Guid rackId, [FromBody] PreflightValidateRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationError((nameof(request), "A request body is required."));
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
        using var scope = _logger.BeginScope(LogScope(rackId, actorId, "preflight-validate"));

        var stopwatch = Stopwatch.StartNew();
        var response = await ValidateCandidateAsync(rackId, request.VlanCatalogue, request.PortIntents, cancellationToken);
        stopwatch.Stop();

        var outcome = response.IsValid
            ? PreflightValidationOutcome.Valid
            : PreflightValidationOutcome.Invalid;
        _metrics.RecordValidate(outcome, stopwatch.Elapsed);

        await WriteAuditAsync(
            rackId, "desired-state.preflight-validated", outcome,
            response.Errors.Count, response.Warnings.Count, response.TopologySnapshotId,
            acknowledgedWarningCodes: null, reasonCode: null, cancellationToken);

        _logger.LogInformation(
            "Pre-flight validation {Outcome} in {DurationMs}ms ({ErrorCount} errors, {WarningCount} warnings).",
            outcome, stopwatch.Elapsed.TotalMilliseconds, response.Errors.Count, response.Warnings.Count);

        return Ok(response);
    }

    /// <summary>
    /// Loads the rack's latest observed inventory, runs the pure validator, and builds the grouped response
    /// with a re-derivable, content-bound run id. Shared shape with the PR gate so both derive identical ids.
    /// </summary>
    private async Task<PreflightValidationResponse> ValidateCandidateAsync(
        Guid rackId,
        IReadOnlyList<VlanCatalogueEntryDto>? vlanCatalogueDto,
        IReadOnlyList<PortAccessIntentDto>? portIntentsDto,
        CancellationToken cancellationToken)
    {
        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        var inventory = RackInventoryProjector.Project(rackId, snapshot);

        var (vlanCatalogue, portIntents) = PreflightContractMappers.ToDomain(vlanCatalogueDto, portIntentsDto);
        var issues = PreflightValidator.Validate(vlanCatalogue, portIntents, inventory, rackId);
        var runId = ValidationRunToken.Compute(rackId, vlanCatalogue, portIntents, inventory.SnapshotId);
        var validatedAt = _time.GetUtcNow().UtcDateTime;

        return PreflightContractMappers.ToResponse(runId, issues, validatedAt, inventory.SnapshotId);
    }

    private async Task WriteAuditAsync(
        Guid rackId, string action, PreflightValidationOutcome outcome,
        int errorCount, int warningCount, Guid? snapshotId,
        IReadOnlyList<string>? acknowledgedWarningCodes, string? reasonCode,
        CancellationToken cancellationToken)
    {
        // Counts + outcome + snapshotId ONLY — never the candidate payload or any secret (NFR4, AC).
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.NetworkConfigAuthor,
            ["correlationId"] = _correlation.CorrelationId,
            ["outcome"] = outcome.ToString().ToLowerInvariant(),
            ["errorCount"] = errorCount,
            ["warningCount"] = warningCount,
            ["topologySnapshotId"] = snapshotId,
        };
        if (acknowledgedWarningCodes is not null)
        {
            details["acknowledgedWarningCodes"] = acknowledgedWarningCodes;
        }

        if (reasonCode is not null)
        {
            details["reasonCode"] = reasonCode;
        }

        var result = outcome is PreflightValidationOutcome.Valid or PreflightValidationOutcome.Created
            ? "success"
            : "rejected";

        await _audit.WriteActionAsync(
            User, rackId, action, "rack-network-intent", rackId.ToString(),
            result, cancellationToken, JsonSerializer.Serialize(details));
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
