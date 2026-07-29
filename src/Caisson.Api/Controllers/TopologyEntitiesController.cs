using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology.Diffing;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only entity detail/history endpoints (AC3): an entity's current representation (from the latest
/// snapshot) plus its change history derived from the stored per-entity diffs (AC2). GET-only, guarded
/// by <see cref="AuthorizationPolicies.TopologyRead"/>.
/// </summary>
/// <remarks>
/// The <c>stableKey</c> is bound with a catch-all (<c>{**stableKey}</c>) because SwitchPort and LLDP
/// keys legitimately contain '/' (e.g. a port id like <c>Ethernet1/0/1</c>). The catch-all must be the
/// terminal segment, so history lives at <c>entities/{entityType}/history/{stableKey}</c> rather than a
/// <c>/history</c> suffix — keeping both endpoints reachable for slash-bearing keys.
/// </remarks>
[ApiController]
[Route("api/racks/{rackId:guid}/topology/entities/{entityType}")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class TopologyEntitiesController : ReadOnlyControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public TopologyEntitiesController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns the entity's latest representation and its stored change history.</summary>
    [HttpGet("{**stableKey}")]
    [ProducesResponseType(typeof(EntityDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntityDetailDto>> GetEntity(
        Guid rackId, string entityType, string stableKey, CancellationToken cancellationToken)
    {
        if (!TryParseEntityType(entityType, out var type))
        {
            return InvalidEntityType(entityType);
        }

        // Finding #4: EntityHistoryAsync had no row cap. The embedded history here is capped to the
        // standard MaxPageSize (no cursor — this combined detail+history view is not itself paginated);
        // GetEntityHistory below is the fully keyset-paginated endpoint for walking a long history.
        var latest = await ResolveLatestFieldsAsync(rackId, type, stableKey, cancellationToken);
        var history = await _context.EntityHistoryAsync(
            rackId, type, stableKey, after: null, RequestPaging.MaxPageSize, cancellationToken);

        if (latest is null && history.Count == 0)
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.entity.read", entityType, stableKey, cancellationToken);
        return Ok(new EntityDetailDto(
            type.ToString(), stableKey, latest, history.Select(ContractMappers.ToEntityDiff).ToList()));
    }

    /// <summary>Returns a paginated page of the entity's stored change history, newest-first.</summary>
    [HttpGet("history/{**stableKey}")]
    [ProducesResponseType(typeof(PagedResult<EntityDiffDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<EntityDiffDto>>> GetEntityHistory(
        Guid rackId, string entityType, string stableKey,
        [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (!TryParseEntityType(entityType, out var type))
        {
            return InvalidEntityType(entityType);
        }

        var endpoint = $"topology.entity.history.{entityType}";
        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var pagingError))
        {
            return ValidationError(pagingError!.Value);
        }

        var page = await _context.EntityHistoryAsync(rackId, type, stableKey, after, limit + 1, cancellationToken);
        if (page.Count == 0 && after is null
            && await ResolveLatestFieldsAsync(rackId, type, stableKey, cancellationToken) is null)
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        var (items, next) = Paginate(page, limit, d => CursorCodec.Encode(d.CreatedAtUtc, d.Id, rackId, endpoint));

        await _audit.WriteReadAsync(User, rackId, "topology.entity.history.read", entityType, stableKey, cancellationToken);
        return Ok(new PagedResult<EntityDiffDto>(items.Select(ContractMappers.ToEntityDiff).ToList(), next));
    }

    private async Task<IReadOnlyDictionary<string, string?>?> ResolveLatestFieldsAsync(
        Guid rackId, TopologyEntityType type, string stableKey, CancellationToken cancellationToken)
    {
        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var byType = TopologyEntityFields.Extract(snapshot);
        return byType.TryGetValue(type, out var entities) && entities.TryGetValue(stableKey, out var fields)
            ? fields
            : null;
    }

    private static bool TryParseEntityType(string value, out TopologyEntityType type)
        => Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);

    private ActionResult InvalidEntityType(string entityType)
        => ValidationError((
            nameof(entityType),
            $"'{entityType}' is not a valid entity type. Expected one of: {string.Join(", ", Enum.GetNames<TopologyEntityType>())}."));

    private ObjectResult EntityNotFound(Guid rackId, string entityType, string stableKey)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Entity not found",
            detail: $"No {entityType} '{stableKey}' was found for rack '{rackId}'.");
}
