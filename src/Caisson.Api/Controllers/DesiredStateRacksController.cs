using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only desired-state rack endpoints (story #62, AC3/AC4): which racks have an active desired-state
/// version, and one rack's active typed rack/switch/port intent tree. GET-only, guarded by
/// <see cref="AuthorizationPolicies.TopologyRead"/>. Keyed by string <c>rackSlug</c>, never the Guid
/// observed-state <c>Rack</c> registry (ADR 0025).
/// </summary>
[ApiController]
[Route("api/desired-state/racks")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DesiredStateRacksController : DesiredStateControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public DesiredStateRacksController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Every rack that has an active desired-state version.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DesiredStateRackSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DesiredStateRackSummaryDto>>> List(CancellationToken cancellationToken)
    {
        var active = await _context.LatestVersionPerRackAsync(cancellationToken);

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.racks.read", "desired-state-racks", null, cancellationToken);
        return Ok(active.Select(DesiredStateContractMappers.ToRackSummary).ToList());
    }

    /// <summary>One rack's active desired-state version and its typed rack/switch/port intent tree.</summary>
    [HttpGet("{rackSlug}/active")]
    [ProducesResponseType(typeof(DesiredStateActiveDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateActiveDto>> GetActive(string rackSlug, CancellationToken cancellationToken)
    {
        var tree = await _context.ActiveVersionWithTreeAsync(rackSlug, cancellationToken);
        if (tree is null)
        {
            return DesiredRackNotFound(rackSlug);
        }

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.rack.active.read", "desired-state-rack", rackSlug, cancellationToken);
        return Ok(DesiredStateContractMappers.ToActive(tree));
    }
}
