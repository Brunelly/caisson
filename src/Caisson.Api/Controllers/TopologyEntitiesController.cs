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
using Microsoft.Extensions.Caching.Memory;

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
    // Finding #14: snapshots are immutable, so a field-map extraction keyed on (rackId, snapshotId) never
    // goes stale — a short TTL only bounds how long a just-superseded latest snapshot's entry lingers.
    private static readonly TimeSpan FieldsCacheTtl = TimeSpan.FromSeconds(30);

    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly IMemoryCache _cache;

    public TopologyEntitiesController(CaissonDbContext context, IBestEffortAuditEventWriter audit, IMemoryCache cache)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
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

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Finding #14: short-circuit the 404 BEFORE any graph load. Every entity that has ever appeared
        // has an "Added" diff row from its first snapshot, so a stable key with no history at all can
        // never have a current representation either — this is a safe, cheap, indexed pre-check.
        if (!await _context.EntityHasHistoryAsync(rackId, type, stableKey, cancellationToken))
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        // Finding #4: EntityHistoryAsync had no row cap. The embedded history here is capped to the
        // standard MaxPageSize (no cursor — this combined detail+history view is not itself paginated);
        // GetEntityHistory below is the fully keyset-paginated endpoint for walking a long history.
        var latest = await ResolveLatestFieldsAsync(rackId, type, stableKey, cancellationToken);
        var history = await _context.EntityHistoryAsync(
            rackId, type, stableKey, after: null, RequestPaging.MaxPageSize, cancellationToken);

        // Finding #29: bmcAddress/managementIp/mgmtAddress are gated behind Operator/Admin.
        var redactedLatest = ContractMappers.RedactManagementFields(latest, IsPrivileged());

        await _audit.WriteReadAsync(User, rackId, "topology.entity.read", entityType, stableKey, cancellationToken);
        return Ok(new EntityDetailDto(
            type.ToString(), stableKey, redactedLatest, history.Select(ContractMappers.ToEntityDiff).ToList()));
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

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Finding #14: the same cheap, indexed short-circuit as GetEntity — a stable key with no history
        // row at all can never have a page to return, on the first page or any later one.
        if (!await _context.EntityHasHistoryAsync(rackId, type, stableKey, cancellationToken))
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        var page = await _context.EntityHistoryAsync(rackId, type, stableKey, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, d => CursorCodec.Encode(d.CreatedAtUtc, d.Id, rackId, endpoint));

        await _audit.WriteReadAsync(User, rackId, "topology.entity.history.read", entityType, stableKey, cancellationToken);
        return Ok(new PagedResult<EntityDiffDto>(items.Select(ContractMappers.ToEntityDiff).ToList(), next));
    }

    private async Task<IReadOnlyDictionary<string, string?>?> ResolveLatestFieldsAsync(
        Guid rackId, TopologyEntityType type, string stableKey, CancellationToken cancellationToken)
    {
        var latestSnapshotId = await _context.LatestSnapshotIdAsync(rackId, cancellationToken);
        if (latestSnapshotId is null)
        {
            return null;
        }

        var byType = await GetOrExtractFieldsAsync(rackId, latestSnapshotId.Value, cancellationToken);
        return byType.TryGetValue(type, out var entities) && entities.TryGetValue(stableKey, out var fields)
            ? fields
            : null;
    }

    /// <summary>
    /// Finding #14: caches the full per-type field-map extraction keyed on (rackId, latestSnapshotId) so
    /// a burst of single-entity reads against the same latest snapshot pays the full-graph load and
    /// <see cref="TopologyEntityFields.Extract"/> walk at most once per <see cref="FieldsCacheTtl"/>,
    /// rather than on every request — safe with no invalidation logic because a snapshot never changes
    /// once written (ADR 0002).
    /// </summary>
    private async Task<IReadOnlyDictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>>
        GetOrExtractFieldsAsync(Guid rackId, Guid latestSnapshotId, CancellationToken cancellationToken)
    {
        var cacheKey = (nameof(TopologyEntitiesController), rackId, latestSnapshotId);
        if (_cache.TryGetValue(cacheKey, out var cached)
            && cached is IReadOnlyDictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> fields)
        {
            return fields;
        }

        var snapshot = await _context.SnapshotWithGraphAsync(rackId, latestSnapshotId, cancellationToken);
        var extracted = snapshot is null
            ? new Dictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>()
            : TopologyEntityFields.Extract(snapshot);

        _cache.Set(cacheKey, extracted, FieldsCacheTtl);
        return extracted;
    }

    /// <summary>Finding #29: whether the caller may see management-plane addresses (Operator/Admin).</summary>
    private bool IsPrivileged() => User.IsInRole(CaissonRoles.Operator) || User.IsInRole(CaissonRoles.Admin);

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
