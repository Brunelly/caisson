using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Orchestration.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Job detail (per-step progress) and cancellation (story #8, AC4, Q3). Detail is readable by any
/// recognised role; cancel is gated to Admin/Operator like the trigger.
/// </summary>
[ApiController]
[Route("api/discovery-jobs")]
[Produces("application/json")]
public sealed class DiscoveryJobDetailController : DiscoveryControllerBase
{
    private readonly IDiscoveryJobService _jobs;
    private readonly IAuditEventWriter _audit;

    public DiscoveryJobDetailController(IDiscoveryJobService jobs, IAuditEventWriter audit)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns a job with its per-step progress and operator-safe error detail (AC4).</summary>
    [HttpGet("{jobId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [ProducesResponseType(typeof(DiscoveryJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscoveryJobDetailDto>> GetById(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null)
        {
            return JobNotFound(jobId);
        }

        await _audit.WriteReadAsync(
            User, job.RackId, "discovery.job.read", "discovery-job", jobId.ToString(), cancellationToken);
        return Ok(DiscoveryContractMappers.ToDetail(job));
    }

    /// <summary>Requests cancellation of a running job (Q3).</summary>
    [HttpPost("{jobId:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.DiscoveryTrigger)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await _jobs.RequestCancellationAsync(jobId, cancellationToken);

        switch (result.Disposition)
        {
            case CancelDisposition.NotFound:
                return JobNotFound(jobId);
            case CancelDisposition.AlreadyTerminal:
                await _audit.WriteActionAsync(
                    User, null, "discovery.job.cancel", "discovery-job", jobId.ToString(),
                    "already-terminal", cancellationToken);
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Job already terminal",
                    detail: $"Job '{jobId}' has already completed and cannot be canceled.");
            default:
                await _audit.WriteActionAsync(
                    User, null, "discovery.job.cancel", "discovery-job", jobId.ToString(),
                    "requested", cancellationToken);
                return Accepted();
        }
    }

    private ObjectResult JobNotFound(Guid jobId)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Job not found",
            detail: $"Discovery job '{jobId}' does not exist.");
}
