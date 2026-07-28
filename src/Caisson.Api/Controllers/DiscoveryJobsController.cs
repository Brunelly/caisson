using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Caisson.Orchestration.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// On-demand trigger + history for a rack's discovery jobs (story #8, AC2/AC4). Trigger is gated to
/// Admin/Operator (<see cref="AuthorizationPolicies.DiscoveryTrigger"/>); the history list is readable by
/// any recognised role (<see cref="AuthorizationPolicies.TopologyRead"/>).
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/discovery-jobs")]
[Produces("application/json")]
public sealed class DiscoveryJobsController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IDiscoveryJobService _jobs;
    private readonly IAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;

    public DiscoveryJobsController(
        CaissonDbContext context,
        IDiscoveryJobService jobs,
        IAuditEventWriter audit,
        ICorrelationContext correlation)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    }

    /// <summary>Triggers an on-demand discovery run for the rack (AC2).</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.DiscoveryTrigger)]
    [ProducesResponseType(typeof(TriggerDiscoveryResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(TriggerDiscoveryResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TriggerDiscoveryResponse>> Trigger(
        Guid rackId, [FromBody] TriggerDiscoveryRequest? request, CancellationToken cancellationToken)
    {
        request ??= new TriggerDiscoveryRequest(null, null, false);

        // Clients may only trigger OnDemand runs; Scheduled is reserved for the scheduler.
        var mode = TriggerType.OnDemand;
        if (!string.IsNullOrWhiteSpace(request.Mode))
        {
            if (!Enum.TryParse(request.Mode, ignoreCase: true, out mode) || mode == TriggerType.Scheduled)
            {
                return ValidationError((nameof(request.Mode), "mode must be 'OnDemand'."));
            }
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var (actorType, actorId) = ResolveActor();
        var result = await _jobs.EnqueueAsync(
            rackId, mode, actorId, actorType, _correlation.CorrelationId,
            request.IdempotencyKey, request.DryRun, cancellationToken);

        await _audit.WriteActionAsync(
            User, rackId, "discovery.job.triggered", "discovery-job", result.JobId.ToString(),
            result.Disposition.ToString(), cancellationToken);

        var body = new TriggerDiscoveryResponse(result.JobId);
        return result.Disposition switch
        {
            EnqueueDisposition.Conflict => Conflict(body),
            _ => Accepted($"/api/discovery-jobs/{result.JobId}", body),
        };
    }

    /// <summary>Lists the rack's discovery jobs, newest first (AC4).</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [ProducesResponseType(typeof(PagedResult<DiscoveryJobSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<DiscoveryJobSummaryDto>>> List(
        Guid rackId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!RequestPaging.TryResolve(pageSize, cursor, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var page = await _jobs.GetJobsPageAsync(rackId, after, limit + 1, cancellationToken);
        var lastSuccess = await _jobs.GetLastSuccessAtUtcAsync(rackId, cancellationToken);
        var (items, next) = Paginate(page, limit, j => CursorCodec.Encode(j.CreatedAtUtc, j.Id));

        await _audit.WriteReadAsync(
            User, rackId, "discovery.jobs.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<DiscoveryJobSummaryDto>(
            items.Select(j => DiscoveryContractMappers.ToSummary(j, lastSuccess)).ToList(), next));
    }

    private (ActorType ActorType, string ActorId) ResolveActor()
    {
        var actorId =
            User.FindFirst("oid")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.Identity?.Name
            ?? "unknown";
        var actorType = User.IsInRole(CaissonRoles.ServiceAccount) ? ActorType.ServiceAccount : ActorType.User;
        return (actorType, actorId);
    }
}
