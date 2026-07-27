using Caisson.Domain.Enums;

namespace Caisson.Domain.Topology;

/// <summary>An observed server within a snapshot. Owns its <see cref="Nic"/> records.</summary>
public sealed class Server : ISnapshotScoped
{
    private readonly List<Nic> _nics = new();

    private Server()
    {
        // EF Core materialization constructor.
        BmcAddress = null!;
    }

    /// <summary>Creates an observed server record.</summary>
    public Server(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        BmcType bmcType,
        string bmcAddress,
        string? bmcUuid = null,
        string? hostname = null)
    {
        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        BmcType = bmcType;
        BmcAddress = bmcAddress;
        BmcUuid = bmcUuid;
        Hostname = hostname;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>The observed BMC management interface type.</summary>
    public BmcType BmcType { get; private set; }

    /// <summary>The observed BMC address.</summary>
    public string BmcAddress { get; private set; }

    /// <summary>Observed BMC/server UUID (natural key, indexed).</summary>
    public string? BmcUuid { get; private set; }

    /// <summary>Observed hostname, if known.</summary>
    public string? Hostname { get; private set; }

    /// <summary>NICs observed on this server.</summary>
    public IReadOnlyCollection<Nic> Nics => _nics;

    /// <summary>Adds an observed NIC to this server.</summary>
    public void AddNic(Nic nic) => _nics.Add(nic);
}
