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
public sealed class AuditController : ControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public AuditController(CaissonDbContext context, IAuditEventWriter audit)
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
        if (!RequestPaging.TryResolve(pageSize, cursor, out var limit, out var after, out var pagingError))
        {
            return ValidationProblem(pagingError!.Value);
        }

        var fromUtc = AsUtc(from) ?? DateTime.UnixEpoch;
        var toUtc = AsUtc(to) ?? DateTime.UtcNow;
        if (fromUtc >= toUtc)
        {
            return ValidationProblem((nameof(from), "'from' must be earlier than 'to'."));
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Rack not found",
                detail: $"Rack '{rackId}' does not exist.");
        }

        var page = await _context.AuditPageAsync(rackId, fromUtc, toUtc, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, a => CursorCodec.Encode(a.OccurredAtUtc, a.Id));

        await _audit.WriteReadAsync(User, rackId, "audit.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<AuditEventDto>(items.Select(ContractMappers.ToAudit).ToList(), next));
    }

    private static DateTime? AsUtc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static (List<T> Items, string? NextCursor) Paginate<T>(
        List<T> page, int limit, Func<T, string> cursorOf)
    {
        if (page.Count <= limit)
        {
            return (page, null);
        }

        var items = page.Take(limit).ToList();
        return (items, cursorOf(items[^1]));
    }

    private ActionResult ValidationProblem((string Field, string Message) error)
    {
        ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }
}
