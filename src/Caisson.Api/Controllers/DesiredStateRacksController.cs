using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only desired-state rack endpoints (story #62, AC3/AC4; story #63, AC1/AC2): which racks have an
/// active desired-state version, and one rack's active typed rack/switch/port intent tree plus its full
/// serialized payload, author metadata, and a strong ETag for caching. GET-only, guarded by
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
    private readonly IBestEffortAuditEventWriter _audit;

    public DesiredStateRacksController(CaissonDbContext context, IBestEffortAuditEventWriter audit)
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

    /// <summary>
    /// One rack's active desired-state version: its typed rack/switch/port intent tree, full serialized
    /// payload, and author metadata (story #63, AC2). Sets a strong <c>ETag</c> derived from the
    /// revision's content hash and honours <c>If-None-Match</c> with a bodyless 304.
    /// </summary>
    [HttpGet("{rackSlug}/active")]
    [ProducesResponseType(typeof(DesiredStateActiveDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateActiveDto>> GetActive(string rackSlug, CancellationToken cancellationToken)
    {
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            return DesiredRackNotFound(rackSlug);
        }

        var tree = await _context.ActiveVersionWithTreeAsync(rackSlug, cancellationToken);
        if (tree is null)
        {
            return DesiredRackNotFound(rackSlug);
        }

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.rack.active.read", "desired-state-rack", rackSlug, cancellationToken);

        SetContentHashETag(tree.Version.ContentHash);
        if (IsNotModified(tree.Version.ContentHash))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(DesiredStateContractMappers.ToActive(tree));
    }
}
