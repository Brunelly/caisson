using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>Read helpers for the deterministic "latest snapshot per rack" access pattern (AC3, NFR1).</summary>
public static class LatestSnapshotQueries
{
    /// <summary>
    /// Returns the latest snapshot for a rack, or <c>null</c> if the rack has none. Ordering is
    /// deterministic (<c>created_at</c> desc, then <c>id</c> desc) and served by the
    /// <c>(rack_id, created_at desc)</c> index. Older snapshots remain queryable.
    /// </summary>
    public static Task<TopologySnapshot?> LatestSnapshotForRackAsync(
        this CaissonDbContext context, Guid rackId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SnapshotSelector
            .OrderByLatest(context.Snapshots.Where(s => s.RackId == rackId))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
