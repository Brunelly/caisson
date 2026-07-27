namespace Caisson.Domain.Topology;

/// <summary>
/// An observed port on a <see cref="Switch"/>. Owns its <see cref="LldpNeighbour"/> records.
/// <see cref="TaggedVlans"/> is an observed-only list mapped to a PostgreSQL <c>integer[]</c> column.
/// </summary>
public sealed class SwitchPort : ISnapshotScoped
{
    private readonly List<LldpNeighbour> _lldpNeighbours = new();

    private SwitchPort()
    {
        // EF Core materialization constructor.
        PortName = null!;
        TaggedVlans = Array.Empty<int>();
    }

    /// <summary>Creates an observed switch-port record.</summary>
    public SwitchPort(
        Guid id,
        Guid switchId,
        Guid rackId,
        Guid snapshotId,
        string portName,
        bool? isUp = null,
        int? pvid = null,
        int[]? taggedVlans = null)
    {
        Id = id;
        SwitchId = switchId;
        RackId = rackId;
        SnapshotId = snapshotId;
        PortName = portName;
        IsUp = isUp;
        Pvid = pvid;
        TaggedVlans = taggedVlans ?? Array.Empty<int>();
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning switch.</summary>
    public Guid SwitchId { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>Observed port name (natural key, unique per switch per snapshot).</summary>
    public string PortName { get; private set; }

    /// <summary>Observed administrative/operational up state, if known.</summary>
    public bool? IsUp { get; private set; }

    /// <summary>Observed port VLAN id (native/untagged), if known.</summary>
    public int? Pvid { get; private set; }

    /// <summary>Observed tagged VLAN ids (mapped to a PostgreSQL <c>integer[]</c> column).</summary>
    public int[] TaggedVlans { get; private set; }

    /// <summary>LLDP neighbours observed on this port.</summary>
    public IReadOnlyCollection<LldpNeighbour> LldpNeighbours => _lldpNeighbours;

    /// <summary>Adds an observed LLDP neighbour to this port.</summary>
    public void AddLldpNeighbour(LldpNeighbour neighbour) => _lldpNeighbours.Add(neighbour);
}
