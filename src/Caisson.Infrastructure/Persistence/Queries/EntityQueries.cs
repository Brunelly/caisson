using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-only helpers for entity latest/history (AC3), served from the stored per-entity diffs so the
/// change history never has to be recomputed (AC2). Composable and <c>AsNoTracking</c>; served by the
/// <c>(rack_id, entity_type, entity_stable_key)</c> index.
/// </summary>
public static class EntityQueries
{
    /// <summary>
    /// Returns the stored change history for one entity (by stable key), newest-first — one row per
    /// snapshot in which the entity was added, removed or modified.
    /// </summary>
    public static Task<List<TopologyEntityDiff>> EntityHistoryAsync(
        this CaissonDbContext context, Guid rackId, TopologyEntityType entityType, string stableKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stableKey);

        return context.EntityDiffs.AsNoTracking()
            .Where(d => d.RackId == rackId && d.EntityType == entityType && d.EntityStableKey == stableKey)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Whether any diff row exists for the entity (used for 404 disambiguation).</summary>
    public static Task<bool> EntityHasHistoryAsync(
        this CaissonDbContext context, Guid rackId, TopologyEntityType entityType, string stableKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stableKey);

        return context.EntityDiffs.AsNoTracking()
            .AnyAsync(
                d => d.RackId == rackId && d.EntityType == entityType && d.EntityStableKey == stableKey,
                cancellationToken);
    }
}
