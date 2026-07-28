using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology.Diffing;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only entity detail/history endpoints (AC3): an entity's current representation (from the latest
/// snapshot) plus its change history derived from the stored per-entity diffs (AC2). GET-only, guarded
/// by <see cref="AuthorizationPolicies.TopologyRead"/>.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/topology/entities/{entityType}/{stableKey}")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class TopologyEntitiesController : ControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public TopologyEntitiesController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns the entity's latest representation and its stored change history.</summary>
    [HttpGet]
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

        var latest = await ResolveLatestFieldsAsync(rackId, type, stableKey, cancellationToken);
        var history = await _context.EntityHistoryAsync(rackId, type, stableKey, cancellationToken);

        if (latest is null && history.Count == 0)
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.entity.read", entityType, stableKey, cancellationToken);
        return Ok(new EntityDetailDto(
            type.ToString(), stableKey, latest, history.Select(ContractMappers.ToEntityDiff).ToList()));
    }

    /// <summary>Returns only the entity's stored change history, newest-first.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<EntityDiffDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<EntityDiffDto>>> GetEntityHistory(
        Guid rackId, string entityType, string stableKey, CancellationToken cancellationToken)
    {
        if (!TryParseEntityType(entityType, out var type))
        {
            return InvalidEntityType(entityType);
        }

        var history = await _context.EntityHistoryAsync(rackId, type, stableKey, cancellationToken);
        if (history.Count == 0
            && await ResolveLatestFieldsAsync(rackId, type, stableKey, cancellationToken) is null)
        {
            return EntityNotFound(rackId, entityType, stableKey);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.entity.history.read", entityType, stableKey, cancellationToken);
        return Ok(history.Select(ContractMappers.ToEntityDiff).ToList());
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
    {
        ModelState.AddModelError(
            nameof(entityType),
            $"'{entityType}' is not a valid entity type. Expected one of: {string.Join(", ", Enum.GetNames<TopologyEntityType>())}.");
        return ValidationProblem(ModelState);
    }

    private ObjectResult EntityNotFound(Guid rackId, string entityType, string stableKey)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Entity not found",
            detail: $"No {entityType} '{stableKey}' was found for rack '{rackId}'.");
}
