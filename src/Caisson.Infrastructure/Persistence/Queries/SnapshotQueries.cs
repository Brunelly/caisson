using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-only, composable snapshot query helpers (AC3, NFR1). All queries are <c>AsNoTracking</c>; the
/// full-graph loads use a split query to avoid a Cartesian explosion across the observed collections.
/// Pagination is keyset by <c>(created_at desc, id desc)</c> — the deterministic
/// <see cref="SnapshotSelector"/> ordering — and served by the <c>(rack_id, created_at desc)</c> index.
/// </summary>
public static class SnapshotQueries
{
    /// <summary>Loads the latest snapshot for a rack with its full observed graph, or <c>null</c>.</summary>
    public static async Task<TopologySnapshot?> LatestSnapshotWithGraphAsync(
        this CaissonDbContext context, Guid rackId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var latestId = await SnapshotSelector
            .OrderByLatest(context.Snapshots.AsNoTracking().Where(s => s.RackId == rackId))
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return latestId is null
            ? null
            : await context.SnapshotWithGraphAsync(rackId, latestId.Value, cancellationToken);
    }

    /// <summary>Loads a specific snapshot (scoped to its rack) with its full observed graph, or <c>null</c>.</summary>
    public static Task<TopologySnapshot?> SnapshotWithGraphAsync(
        this CaissonDbContext context, Guid rackId, Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Snapshots
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Switches).ThenInclude(sw => sw.Ports).ThenInclude(p => p.LldpNeighbours)
            .Include(s => s.Servers).ThenInclude(sv => sv.Nics)
            .Include(s => s.Vlans)
            .Include(s => s.CandidateMappings)
            .Include(s => s.ChangeSummary)
            .FirstOrDefaultAsync(s => s.RackId == rackId && s.Id == snapshotId, cancellationToken);
    }

    /// <summary>
    /// Loads a page of snapshot metadata (with change summary) for a rack, newest-first. When
    /// <paramref name="afterCreatedAtUtc"/> is supplied the page continues strictly after that keyset
    /// position. One extra row is fetched to let the caller decide whether a further page exists.
    /// </summary>
    public static Task<List<TopologySnapshot>> SnapshotHistoryPageAsync(
        this CaissonDbContext context, Guid rackId, DateTime? afterCreatedAtUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.Snapshots.AsNoTracking()
            .Include(s => s.ChangeSummary)
            .Where(s => s.RackId == rackId);

        // Keyset on created_at (effectively unique per snapshot); id remains the deterministic tie-break
        // in the ordering below.
        if (afterCreatedAtUtc is { } cursor)
        {
            query = query.Where(s => s.CreatedAtUtc < cursor);
        }

        return SnapshotSelector.OrderByLatest(query)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Returns whether the rack exists (for 404 disambiguation).</summary>
    public static Task<bool> RackExistsAsync(
        this CaissonDbContext context, Guid rackId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Racks.AsNoTracking().AnyAsync(r => r.Id == rackId, cancellationToken);
    }
}
