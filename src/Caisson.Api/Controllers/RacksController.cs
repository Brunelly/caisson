using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Api.Controllers;

/// <summary>Lists observed racks visible to the current principal.</summary>
[ApiController]
[Route("api/racks")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class RacksController : ReadOnlyControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IRackAccessPolicy _rackAccess;
    private readonly IBestEffortAuditEventWriter _audit;

    public RacksController(CaissonDbContext context, IRackAccessPolicy rackAccess, IBestEffortAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _rackAccess = rackAccess ?? throw new ArgumentNullException(nameof(rackAccess));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RackSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RackSummaryDto>>> Get(CancellationToken cancellationToken)
    {
        var candidates = await _context.Racks.AsNoTracking()
            .OrderBy(rack => rack.Name)
            .ThenBy(rack => rack.ExternalKey)
            .ThenBy(rack => rack.Id)
            .Select(rack => new RackSummaryDto(rack.Id, rack.ExternalKey, rack.Name))
            .ToListAsync(cancellationToken);

        var visible = new List<RackSummaryDto>(candidates.Count);
        foreach (var rack in candidates)
        {
            if (await _rackAccess.CanReadAsync(User, rack.Id, cancellationToken))
            {
                visible.Add(rack);
            }
        }

        await _audit.WriteReadAsync(User, null, "racks.catalogue.read", "rack-catalogue", null, cancellationToken);
        return Ok(visible);
    }
}
