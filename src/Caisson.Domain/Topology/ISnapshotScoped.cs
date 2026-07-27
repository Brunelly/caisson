namespace Caisson.Domain.Topology;

/// <summary>
/// Marks an observed entity that belongs to exactly one <see cref="TopologySnapshot"/> and one rack.
/// Every observed record carries both keys so that rack-level isolation (<c>WHERE rack_id = ?</c>,
/// NFR4) and indexed snapshot-scoped joins are straightforward, and so the append-only immutability
/// guard can recognise snapshot content generically. <see cref="Rack"/> is deliberately the one
/// stable registry entity and does <b>not</b> implement this interface.
/// </summary>
public interface ISnapshotScoped
{
    /// <summary>The snapshot this observed record belongs to.</summary>
    Guid SnapshotId { get; }

    /// <summary>The rack this observed record belongs to (denormalized for isolation and indexing).</summary>
    Guid RackId { get; }
}
