namespace Caisson.Domain.Topology;

/// <summary>An observed LLDP neighbour reported on a <see cref="SwitchPort"/>.</summary>
public sealed class LldpNeighbour : ISnapshotScoped
{
    private LldpNeighbour()
    {
        // EF Core materialization constructor.
        ChassisId = null!;
        PortId = null!;
    }

    /// <summary>Creates an observed LLDP neighbour record.</summary>
    public LldpNeighbour(
        Guid id,
        Guid switchPortId,
        Guid rackId,
        Guid snapshotId,
        string chassisId,
        string portId,
        string? systemName = null,
        string? mgmtAddress = null)
    {
        Id = id;
        SwitchPortId = switchPortId;
        RackId = rackId;
        SnapshotId = snapshotId;
        ChassisId = chassisId;
        PortId = portId;
        SystemName = systemName;
        MgmtAddress = mgmtAddress;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning switch port.</summary>
    public Guid SwitchPortId { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>Observed LLDP chassis id of the neighbour.</summary>
    public string ChassisId { get; private set; }

    /// <summary>Observed LLDP port id of the neighbour.</summary>
    public string PortId { get; private set; }

    /// <summary>Observed neighbour system name, if advertised.</summary>
    public string? SystemName { get; private set; }

    /// <summary>Observed neighbour management address, if advertised.</summary>
    public string? MgmtAddress { get; private set; }
}
