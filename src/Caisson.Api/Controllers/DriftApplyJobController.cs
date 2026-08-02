using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Shaping;
using Caisson.Orchestration.DriftApply;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Drift-apply job status/listing (story #65, AC4). Readable by any recognised role
/// (<see cref="AuthorizationPolicies.TopologyRead"/>) — a read-only persona may view outcomes/audit even
/// though it can never trigger an apply. Reconnect recovery (AC7) is this GET endpoint: a client that
/// missed live events while disconnected re-syncs current status here before resuming the live stream.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/jobs")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DriftApplyJobController : ReadOnlyControllerBase
{
    private const string SupportedType = "DriftApply";

    private readonly IDriftApplyJobService _jobs;
    private readonly IBestEffortAuditEventWriter _audit;

    public DriftApplyJobController(IDriftApplyJobService jobs, IBestEffortAuditEventWriter audit)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Returns a drift-apply job's per-step progress and terminal outcome (before/after VLAN, reason
    /// code) without secrets (AC4/NFR4).
    /// </summary>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(DriftApplyJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DriftApplyJobDetailDto>> GetById(Guid rackId, Guid jobId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null || job.RackId != rackId)
        {
            return JobNotFound(rackId, jobId);
        }

        await _audit.WriteReadAsync(User, rackId, "drift.apply.job.read", "drift-apply-job", jobId.ToString(), cancellationToken);
        return Ok(DriftApplyContractMappers.ToDetail(job));
    }

    /// <summary>Lists a rack's drift-apply jobs, newest first, optionally filtered by <paramref name="state"/>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DriftApplyJobSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<DriftApplyJobSummaryDto>>> List(
        Guid rackId,
        [FromQuery] string? type,
        [FromQuery] string? state,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "drift-apply-jobs.list";

        if (!string.IsNullOrEmpty(type) && !string.Equals(type, SupportedType, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationError((nameof(type), $"type must be '{SupportedType}' when specified."));
        }

        DriftApplyJobStatus? stateFilter = null;
        if (!string.IsNullOrEmpty(state))
        {
            if (!Enum.TryParse<DriftApplyJobStatus>(state, ignoreCase: true, out var parsed))
            {
                return ValidationError((nameof(state),
                    $"state must be one of: {string.Join(", ", Enum.GetNames<DriftApplyJobStatus>())}."));
            }

            stateFilter = parsed;
        }

        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var page = await _jobs.GetJobsPageAsync(rackId, stateFilter, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, j => CursorCodec.Encode(j.RequestedAtUtc, j.Id, rackId, endpoint));

        await _audit.WriteReadAsync(User, rackId, "drift.apply.jobs.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<DriftApplyJobSummaryDto>(items.Select(DriftApplyContractMappers.ToSummary).ToList(), next));
    }

    private ObjectResult JobNotFound(Guid rackId, Guid jobId)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Job not found",
            detail: $"Drift-apply job '{jobId}' was not found for rack '{rackId}'.");
}
