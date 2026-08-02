using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Observability;
using Caisson.Api.Security;
using Caisson.Api.Services;
using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Caisson.Api.Controllers;

/// <summary>
/// Rack-scoped desired-state impact-preview endpoints (story #171, Tasks #196/#202). Computes a server-side
/// diff between the rack's latest ingested desired-state revision (baseline) and a candidate YAML and
/// returns a raw unified diff plus a structured summary with topology deep-link references. Modelled on
/// <see cref="DesiredStatePreflightController"/> exactly (4-step order: rack access → rack existence → work →
/// counts-only audit), but gated by <see cref="AuthorizationPolicies.TopologyRead"/> so Read Only users can
/// preview (persona 3 / AC3). Cross-rack access follows the codebase's leak-safe convention: an
/// inaccessible rack returns 404 (no existence oracle, ADR 0013 / <see cref="DiscoveryControllerBase"/>)
/// rather than AC4's literal 403 — this fully satisfies NFR2 (no baseline/diff returned); see ADR 0055.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/desired-state")]
[Produces("application/json")]
public sealed class DesiredStateImpactPreviewController : DiscoveryControllerBase
{
    /// <summary>The 409 reason code returned when a rack has no ingested baseline revision (AC5).</summary>
    public const string MissingBaselineReasonCode = "DESIRED_STATE_BASELINE_MISSING";

    private readonly CaissonDbContext _context;
    private readonly ImpactPreviewService _service;
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly ImpactPreviewMetrics _metrics;
    private readonly ILogger<DesiredStateImpactPreviewController> _logger;

    public DesiredStateImpactPreviewController(
        CaissonDbContext context,
        ImpactPreviewService service,
        IBestEffortAuditEventWriter audit,
        ICorrelationContext correlation,
        ImpactPreviewMetrics metrics,
        ILogger<DesiredStateImpactPreviewController> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes (or serves from cache) the impact preview for a candidate YAML. Returns 200 with the raw
    /// diff + structured summary, 400 with line/column issues for invalid YAML (no cache row written), or
    /// 409 with a reason code + ingestion guidance when the rack has no baseline revision.
    /// </summary>
    [HttpPost("impact-preview")]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [EnableRateLimiting(RateLimitPolicies.NetworkConfigRoundTrip)]
    [RequestSizeLimit(DesiredStateSchema.MaxYamlDocumentBytes)]
    [ProducesResponseType(typeof(ImpactPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MissingBaselineResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImpactPreviewResponse>> Preview(
        Guid rackId, [FromBody] ImpactPreviewRequest? request, CancellationToken cancellationToken)
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
        using var scope = _logger.BeginScope(LogScope(rackId, actorId, "impact-preview"));

        var stopwatch = Stopwatch.StartNew();
        var result = await _service.PreviewAsync(rackId, request.Yaml, actorId, cancellationToken);
        stopwatch.Stop();

        switch (result.Status)
        {
            case ImpactPreviewStatus.InvalidYaml:
                _metrics.RecordRejected(ImpactPreviewOutcome.Invalid);
                await WriteAuditAsync(
                    rackId, ImpactPreviewOutcome.Invalid, cacheHit: false, row: null,
                    issueCount: result.Issues!.Count, cancellationToken);
                _logger.LogInformation(
                    "Impact preview rejected (invalid YAML) with {IssueCount} issue(s) in {DurationMs}ms.",
                    result.Issues!.Count, stopwatch.Elapsed.TotalMilliseconds);
                return ImportIssues(result.Issues);

            case ImpactPreviewStatus.MissingBaseline:
                _metrics.RecordRejected(ImpactPreviewOutcome.MissingBaseline);
                await WriteAuditAsync(
                    rackId, ImpactPreviewOutcome.MissingBaseline, cacheHit: false, row: null,
                    issueCount: 0, cancellationToken);
                _logger.LogInformation("Impact preview rejected: no baseline revision for rack.");
                return MissingBaseline();

            default:
                var row = result.Row!;
                if (result.CacheHit)
                {
                    _metrics.RecordCacheHit();
                }
                else
                {
                    _metrics.RecordCompute(ImpactPreviewOutcome.Success, result.DiffComputeDuration);
                }

                await WriteAuditAsync(
                    rackId, ImpactPreviewOutcome.Success, result.CacheHit, row, issueCount: 0, cancellationToken);

                _logger.LogInformation(
                    "Impact preview {Result} rackId={RackId} baselineRevisionId={BaselineRevisionId} "
                    + "candidateHash={CandidateHash} cacheHit={CacheHit} computeMs={ComputeMs}.",
                    result.CacheHit ? "cache-hit" : "computed", rackId, row.BaselineRevisionId,
                    row.CandidateSha256, result.CacheHit, result.DiffComputeDuration.TotalMilliseconds);

                SetContentHashETag(row.CandidateSha256);
                return Ok(ImpactPreviewContractMappers.ToResponse(row, result.CacheHit));
        }
    }

    /// <summary>
    /// Resolves a previously-computed impact preview by its candidate id (the cache row id), scoped to the
    /// rack (leak-safe GET, NFR2). Returns 200 with the stored diff + summary, or 404 when no such row exists
    /// for this rack.
    /// </summary>
    [HttpGet("candidates/{candidateId:guid}/impact-preview")]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [ProducesResponseType(typeof(ImpactPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImpactPreviewResponse>> GetByCandidate(
        Guid rackId, Guid candidateId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var row = await _service.GetByIdAsync(rackId, candidateId, cancellationToken);
        if (row is null)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Impact preview not found",
                detail: $"No impact preview '{candidateId}' exists for rack '{rackId}'.");
        }

        if (IsNotModified(row.CandidateSha256))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        SetContentHashETag(row.CandidateSha256);
        return Ok(ImpactPreviewContractMappers.ToResponse(row, cacheHit: true));
    }

    private ActionResult MissingBaseline()
        => Conflict(new MissingBaselineResponse(
            MissingBaselineReasonCode,
            "This rack has no ingested desired-state revision yet. Ingest an initial baseline via the "
            + "desired-state git ingestion flow before requesting an impact preview."));

    /// <summary>Maps accumulated import issues onto an RFC7807 ValidationProblem carrying path + line/column.</summary>
    private ActionResult ImportIssues(IReadOnlyList<Ingestion.RoundTrip.DesiredStateImportIssue> issues)
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

    private static string MessageWithPosition(Ingestion.RoundTrip.DesiredStateImportIssue issue)
        => issue is { Line: { } line, Column: { } column }
            ? $"{issue.Message} (line {line}, column {column})"
            : issue.Message;

    /// <summary>Sets a strong <c>ETag</c> derived from the cached row's candidate content hash (AC2 fast path).</summary>
    private void SetContentHashETag(string contentHash)
        => Response.GetTypedHeaders().ETag = new EntityTagHeaderValue($"\"{contentHash}\"");

    private bool IsNotModified(string contentHash)
    {
        var ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch is null || ifNoneMatch.Count == 0)
        {
            return false;
        }

        var etag = new EntityTagHeaderValue($"\"{contentHash}\"");
        foreach (var candidate in ifNoneMatch)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task WriteAuditAsync(
        Guid rackId, ImpactPreviewOutcome outcome, bool cacheHit,
        DesiredStateCandidateDiffCache? row, int issueCount, CancellationToken cancellationToken)
    {
        // Counts + hashes + cacheHit ONLY — never the candidate YAML or the diff/summary body (NFR4, AC).
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.TopologyRead,
            ["correlationId"] = _correlation.CorrelationId,
            ["outcome"] = outcome.ToString().ToLowerInvariant(),
            ["cacheHit"] = cacheHit,
            ["issueCount"] = issueCount,
        };
        if (row is not null)
        {
            details["candidateId"] = row.Id;
            details["candidateSha256"] = row.CandidateSha256;
            details["baselineSha256"] = row.BaselineSha256;
            details["baselineRevisionId"] = row.BaselineRevisionId;
        }

        await _audit.WriteActionAsync(
            User, rackId, "desired-state.impact-previewed", "rack-desired-state", rackId.ToString(),
            outcome == ImpactPreviewOutcome.Success ? "success" : "rejected",
            cancellationToken, JsonSerializer.Serialize(details));
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
