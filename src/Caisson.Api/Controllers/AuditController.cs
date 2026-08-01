using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only audit-trail endpoint (AC3): audit events for a rack within a time range, newest-first and
/// keyset-paginated. Returns both discovery and API-access events. GET-only, guarded by
/// <see cref="AuthorizationPolicies.TopologyRead"/>.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/audit")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class AuditController : ReadOnlyControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;

    public AuditController(CaissonDbContext context, IBestEffortAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns a paginated audit trail for a rack within an optional time range.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AuditEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<AuditEventDto>>> GetAudit(
        Guid rackId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "audit.list";
        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var pagingError))
        {
            return ValidationError(pagingError!.Value);
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var fromUtc = AsUtc(from) ?? DateTime.UnixEpoch;
        var toUtc = AsUtc(to) ?? DateTime.UtcNow;
        if (fromUtc >= toUtc)
        {
            return ValidationError((nameof(from), "'from' must be earlier than 'to'."));
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var page = await _context.AuditPageAsync(rackId, fromUtc, toUtc, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, a => CursorCodec.Encode(a.OccurredAtUtc, a.Id, rackId, endpoint));

        await _audit.WriteReadAsync(User, rackId, "audit.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<AuditEventDto>(items.Select(ContractMappers.ToAudit).ToList(), next));
    }

    // 'from'/'to' are interpreted as UTC instants. A value carrying a non-UTC kind (e.g. a client sends an
    // offset) is converted to the equivalent UTC instant rather than merely relabelled, so the filter
    // window is not shifted by the offset.
    private static DateTime? AsUtc(DateTime? value)
        => value is { } v
            ? (v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime())
            : null;
}
