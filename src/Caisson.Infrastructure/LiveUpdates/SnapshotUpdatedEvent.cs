namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Broadcast when a discovery run persists a new snapshot for a rack (story #9, AC1). Emitted from the
/// single atomic ingestion choke point right after the successful <c>SaveChangesAsync</c>, so no persist
/// path can be missed. <see cref="Seq"/> reuses the DB-guaranteed monotonic per-rack <see cref="Version"/>
/// (unique index <c>ux_topology_snapshot_rack_id_version</c>), which is free and cluster-consistent.
/// </summary>
/// <param name="RackId">The stable rack whose topology changed.</param>
/// <param name="JobId">The discovery job that produced the snapshot, when known (optional).</param>
/// <param name="SnapshotId">The new snapshot's id — clients refetch the latest snapshot to get detail.</param>
/// <param name="Version">The monotonic per-rack snapshot version.</param>
/// <param name="Summary">A counts-only summary (never the graph or raw device data).</param>
/// <param name="Timestamp">When the snapshot was persisted (UTC).</param>
/// <param name="Seq">The per-rack ordering sequence (equal to <paramref name="Version"/>).</param>
/// <param name="CorrelationId">The correlation id of the discovery run.</param>
public sealed record SnapshotUpdatedEvent(
    Guid RackId,
    Guid? JobId,
    Guid SnapshotId,
    int Version,
    SnapshotSummary Summary,
    DateTimeOffset Timestamp,
    long Seq,
    Guid CorrelationId) : TopologyEvent;
