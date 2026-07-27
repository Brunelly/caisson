namespace Caisson.Domain.Topology;

/// <summary>
/// A rack is the single <b>stable</b> registry entity in the observed-state model: it persists across
/// discovery runs so that "latest snapshot for a rack" is deterministic and rack-level isolation
/// (<c>WHERE rack_id = ?</c>) is well-defined. It deliberately does <b>not</b> implement
/// <see cref="ISnapshotScoped"/>; everything else in the observed graph is denormalized per snapshot.
/// </summary>
public sealed class Rack
{
    private readonly List<TopologySnapshot> _snapshots = new();

    private Rack()
    {
        // EF Core materialization constructor.
        ExternalKey = null!;
        Name = null!;
    }

    /// <summary>Creates a stable rack registry entry.</summary>
    public Rack(Guid id, string externalKey, string name, DateTime createdAtUtc)
    {
        Id = id;
        ExternalKey = externalKey;
        Name = name;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Stable primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Stable external identity of the rack (unique across the deployment).</summary>
    public string ExternalKey { get; private set; }

    /// <summary>Human-readable rack name.</summary>
    public string Name { get; private set; }

    /// <summary>When the rack was first registered.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Snapshots captured for this rack, newest determined by <see cref="SnapshotSelector"/>.</summary>
    public IReadOnlyCollection<TopologySnapshot> Snapshots => _snapshots;
}
