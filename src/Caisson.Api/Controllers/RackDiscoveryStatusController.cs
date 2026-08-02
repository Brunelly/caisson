using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Orchestration.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// A rack's at-a-glance discovery status: latest job, last-success time and schedule state (story #8,
/// AC4). Readable by any recognised role.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/discovery-status")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class RackDiscoveryStatusController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IDiscoveryJobService _jobs;
    private readonly IBestEffortAuditEventWriter _audit;

    public RackDiscoveryStatusController(
        CaissonDbContext context, IDiscoveryJobService jobs, IBestEffortAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns the rack's discovery status summary (AC4).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(DiscoveryStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscoveryStatusDto>> GetStatus(Guid rackId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var summary = await _jobs.GetStatusAsync(rackId, cancellationToken);
        await _audit.WriteReadAsync(
            User, rackId, "discovery.status.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(DiscoveryContractMappers.ToStatus(summary));
    }
}
