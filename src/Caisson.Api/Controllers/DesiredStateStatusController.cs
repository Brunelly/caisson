using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only desired-state ingestion status endpoint (story #62, AC4 — "the Operator wants to see
/// whether the latest desired-state commit was ingested successfully"). GET-only, guarded by
/// <see cref="AuthorizationPolicies.TopologyRead"/>.
/// </summary>
[ApiController]
[Route("api/desired-state")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DesiredStateStatusController : DesiredStateControllerBase
{
    /// <summary>Overall status reported when ingestion has never run.</summary>
    private const string NeverRun = "NeverRun";

    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public DesiredStateStatusController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Last successful/attempted ingestion time, latest commit SHA, and overall status.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(DesiredStateStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DesiredStateStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var latest = await _context.LatestIngestionRunAsync(cancellationToken);
        var lastSuccess = await _context.LastSuccessfulIngestionAtUtcAsync(cancellationToken);

        var status = new DesiredStateStatusDto(
            lastSuccess, latest?.StartedAtUtc, latest?.CommitSha, latest?.Status.ToString() ?? NeverRun);

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.status.read", "desired-state-status", null, cancellationToken);
        return Ok(status);
    }
}
