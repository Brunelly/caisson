namespace Caisson.Domain.Topology;

/// <summary>An observed VLAN within a snapshot.</summary>
public sealed class Vlan : ISnapshotScoped
{
    private Vlan()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates an observed VLAN record.</summary>
    public Vlan(Guid id, Guid rackId, Guid snapshotId, int vlanId, string? name = null)
    {
        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        VlanId = vlanId;
        Name = name;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>The observed 802.1Q VLAN id (indexed per snapshot).</summary>
    public int VlanId { get; private set; }

    /// <summary>Observed VLAN name, if known.</summary>
    public string? Name { get; private set; }
}
