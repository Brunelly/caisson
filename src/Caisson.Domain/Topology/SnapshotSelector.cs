namespace Caisson.Domain.Topology;

/// <summary>
/// Deterministic "latest snapshot" selection. The ordering is <c>created_at</c> descending, then
/// <c>id</c> descending as a tie-breaker for the (rare) case of identical timestamps — matching AC3.
/// Pure LINQ so it composes over both in-memory sequences and EF <see cref="IQueryable{T}"/>.
/// </summary>
public static class SnapshotSelector
{
    /// <summary>Orders snapshots newest-first: by <c>CreatedAtUtc</c> desc, then <c>Id</c> desc.</summary>
    public static IOrderedEnumerable<TopologySnapshot> OrderByLatest(
        IEnumerable<TopologySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id);
    }

    /// <summary>Orders snapshots newest-first over an <see cref="IQueryable{T}"/> (server-side).</summary>
    public static IOrderedQueryable<TopologySnapshot> OrderByLatest(
        IQueryable<TopologySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id);
    }

    /// <summary>Returns the single latest snapshot, or <c>null</c> when the sequence is empty.</summary>
    public static TopologySnapshot? Latest(IEnumerable<TopologySnapshot> snapshots)
        => OrderByLatest(snapshots).FirstOrDefault();
}
