using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Api.Services;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.RoundTrip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Caisson.Api.Controllers;

/// <summary>
/// The gated desired-state PR-creation endpoint (story #170, AC3/AC5). It RE-RUNS the pure validator on the
/// submitted candidate against the current latest snapshot and re-derives the content-bound
/// <c>validationRunId</c> — the client's <c>validationRunId</c>/counts are never trusted. It blocks with a
/// structured 422 on a run-id mismatch (candidate or topology changed), on any error, or on any
/// unacknowledged/stale warning code; only a fully-acknowledged, still-current candidate reaches the
/// (stubbed) publisher. Side-effect free except the audit write — no git write occurs (#172 deferred).
/// Modelled on <see cref="DriftApplyController"/>'s gated-write pattern.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/desired-state")]
[Produces("application/json")]
public sealed class DesiredStatePrController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IDesiredStatePrService _prService;
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly PreflightValidationMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<DesiredStatePrController> _logger;

    public DesiredStatePrController(
        CaissonDbContext context,
        IDesiredStatePrService prService,
        IBestEffortAuditEventWriter audit,
        ICorrelationContext correlation,
        PreflightValidationMetrics metrics,
        TimeProvider time,
        ILogger<DesiredStatePrController> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("prs")]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [EnableRateLimiting(RateLimitPolicies.NetworkConfigRoundTrip)]
    [RequestSizeLimit(DesiredStateSchema.MaxYamlDocumentBytes)]
    [ProducesResponseType(typeof(CreatePrResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CreatePrResponse>> CreatePr(
        Guid rackId, [FromBody] CreatePrRequest? request, CancellationToken cancellationToken)
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
        using var scope = _logger.BeginScope(LogScope(rackId, actorId, "create-pr"));

        var stopwatch = Stopwatch.StartNew();

        // Re-run the full validator server-side against the CURRENT latest snapshot; never trust the client.
        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        var inventory = RackInventoryProjector.Project(rackId, snapshot);
        var (vlanCatalogue, portIntents) = PreflightContractMappers.ToDomain(request.VlanCatalogue, request.PortIntents);
        var issues = PreflightValidator.Validate(vlanCatalogue, portIntents, inventory, rackId);
        var recomputedRunId = ValidationRunToken.Compute(rackId, vlanCatalogue, portIntents, inventory.SnapshotId);
        var response = PreflightContractMappers.ToResponse(
            recomputedRunId, issues, _time.GetUtcNow().UtcDateTime, inventory.SnapshotId);

        var rejection = EvaluateGate(request, response, recomputedRunId);
        if (rejection is { } reject)
        {
            stopwatch.Stop();
            _metrics.RecordCreatePr(PreflightValidationOutcome.Rejected, stopwatch.Elapsed);
            await WriteAuditAsync(
                rackId, "desired-state.pr-rejected", "rejected", PreflightValidationOutcome.Rejected,
                response.Errors.Count, response.Warnings.Count, response.TopologySnapshotId,
                request.AcknowledgedWarningCodes, reject.ReasonCode, cancellationToken);

            _logger.LogInformation(
                "Desired-state PR rejected ({ReasonCode}) with {ErrorCount} errors, {WarningCount} warnings.",
                reject.ReasonCode, response.Errors.Count, response.Warnings.Count);

            return GateRejection(reject, response);
        }

        DesiredStatePrCreationResult result;
        try
        {
            result = await _prService.CreatePullRequestAsync(
                new DesiredStatePrCreationRequest(
                    rackId, recomputedRunId, vlanCatalogue, portIntents,
                    request.AcknowledgedWarningCodes ?? Array.Empty<string>()),
                cancellationToken);
        }
        catch (DesiredStateRenderException ex)
        {
            // The candidate cannot be rendered to a valid desired-state document (e.g. a non-DNS-shaped rack
            // slug) — a 422, never a git write. The publisher performed no side effect.
            stopwatch.Stop();
            _metrics.RecordCreatePr(PreflightValidationOutcome.Rejected, stopwatch.Elapsed);
            return RenderRejection(ex);
        }
        catch (PrOnlyGuardrailViolationException)
        {
            // The PR-only guardrail refused the request (AC3). The publisher already audited the refusal.
            stopwatch.Stop();
            _metrics.RecordCreatePr(PreflightValidationOutcome.Rejected, stopwatch.Elapsed);
            _logger.LogInformation("Desired-state PR refused by the PR-only guardrail.");
            return GuardrailViolation();
        }

        stopwatch.Stop();
        _metrics.RecordCreatePr(PreflightValidationOutcome.Created, stopwatch.Elapsed);

        if (result.ErrorCode is { } errorCode)
        {
            // The gate passed but the git write failed; the publisher audited git.pr.failed. Surface a stable
            // error code with no secret text (AC6).
            _logger.LogWarning("Desired-state PR creation failed ({ErrorCode}).", errorCode);
            return PublisherFailure(errorCode);
        }

        _logger.LogInformation(
            "Desired-state PR {Status} (reused={Reused}, prNumber={PrNumber}).",
            result.Status, result.Reused, result.PullRequestNumber);

        return Accepted(new CreatePrResponse(
            recomputedRunId, result.Status, result.Detail, result.PullRequestUrl,
            result.PullRequestNumber, result.BranchName, result.CommitSha, result.CandidateFingerprint,
            result.Reused, result.RepoOwner, result.RepoName, result.ErrorCode, result.ChangeSummary));
    }

    /// <summary>Maps a candidate that cannot be rendered to a 422 with a stable reason code.</summary>
    private ObjectResult RenderRejection(DesiredStateRenderException ex)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The candidate cannot be rendered to a desired-state document",
            Detail = "The candidate could not be rendered to a valid desired-state document; no pull request was created.",
        };
        problem.Extensions["reasonCode"] = "render";
        problem.Extensions["correlationId"] = _correlation.CorrelationId;
        problem.Extensions["errors"] = ex.Errors.Select(e => new { field = e.Field, message = e.Message });
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status422UnprocessableEntity };
    }

    /// <summary>Maps a PR-only guardrail violation to a 409 RFC7807 problem (AC3).</summary>
    private ObjectResult GuardrailViolation()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "PR-only guardrail violation",
            Detail = GitPrErrorCodes.MessageFor(GitPrErrorCodes.PrOnlyGuardrailViolation),
        };
        problem.Extensions["errorCode"] = GitPrErrorCodes.PrOnlyGuardrailViolation;
        problem.Extensions["reasonCode"] = GitPrErrorCodes.PrOnlyGuardrailViolation;
        problem.Extensions["correlationId"] = _correlation.CorrelationId;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }

    /// <summary>Maps a publisher failure error code to a stable RFC7807 problem for UI display + triage (AC6).</summary>
    private ObjectResult PublisherFailure(string errorCode)
    {
        var status = errorCode switch
        {
            GitPrErrorCodes.GitHubApiFailed or GitPrErrorCodes.GitCredentialsUnavailable => StatusCodes.Status502BadGateway,
            // A deployment-configuration gap (owner/name unset) is a server-side condition, not a client error.
            GitPrErrorCodes.GitRepoNotConfigured => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError,
        };
        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Pull-request creation failed",
            Detail = GitPrErrorCodes.MessageFor(errorCode),
        };
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["correlationId"] = _correlation.CorrelationId;
        return new ObjectResult(problem) { StatusCode = status };
    }

    /// <summary>Applies the TOCTOU-safe gate: run-id match, then no errors, then all warnings acknowledged.</summary>
    private static GateReject? EvaluateGate(CreatePrRequest request, PreflightValidationResponse response, string recomputedRunId)
    {
        if (!string.Equals(request.ValidationRunId, recomputedRunId, StringComparison.Ordinal))
        {
            return new GateReject(
                "revalidate", "Validation run is stale",
                "The candidate or the rack topology changed since it was validated. Re-run pre-flight "
                + "validation, then create the pull request from the fresh result.");
        }

        if (response.Errors.Count > 0)
        {
            return new GateReject(
                "errors", "Blocking validation errors",
                "The candidate has blocking validation errors and cannot be turned into a pull request. "
                + "Resolve every error, then re-validate.");
        }

        var warningCodes = response.Warnings.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
        var acknowledged = (request.AcknowledgedWarningCodes ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var unacknowledged = warningCodes.Where(c => !acknowledged.Contains(c)).ToList();
        var stale = acknowledged.Where(c => !warningCodes.Contains(c)).ToList();

        if (unacknowledged.Count > 0 || stale.Count > 0)
        {
            return new GateReject(
                "acknowledge-warnings", "Safety warnings require acknowledgement",
                "Every safety warning must be acknowledged (and no stale acknowledgements supplied) before "
                + "a pull request can be created.");
        }

        return null;
    }

    private ObjectResult GateRejection(GateReject reject, PreflightValidationResponse response)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = reject.Title,
            Detail = reject.Detail,
        };
        problem.Extensions["reasonCode"] = reject.ReasonCode;
        problem.Extensions["issues"] = response;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status422UnprocessableEntity };
    }

    private async Task WriteAuditAsync(
        Guid rackId, string action, string result, PreflightValidationOutcome outcome,
        int errorCount, int warningCount, Guid? snapshotId,
        IReadOnlyList<string>? acknowledgedWarningCodes, string? reasonCode,
        CancellationToken cancellationToken)
    {
        // Counts + outcome + acknowledged codes ONLY — never the candidate payload or any secret (NFR4, AC).
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.NetworkConfigAuthor,
            ["correlationId"] = _correlation.CorrelationId,
            ["outcome"] = outcome.ToString().ToLowerInvariant(),
            ["errorCount"] = errorCount,
            ["warningCount"] = warningCount,
            ["topologySnapshotId"] = snapshotId,
            ["acknowledgedWarningCodes"] = acknowledgedWarningCodes ?? Array.Empty<string>(),
        };
        if (reasonCode is not null)
        {
            details["reasonCode"] = reasonCode;
        }

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

    private readonly record struct GateReject(string ReasonCode, string Title, string Detail);
}
