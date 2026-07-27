using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Domain.Topology;

/// <summary>
/// An observed network interface on a <see cref="Server"/>. Owns its <see cref="MacAddress"/> records
/// (a NIC may present multiple MACs). The primary MAC is stored normalized as a
/// <see cref="MacAddressValue"/>.
/// </summary>
public sealed class Nic : ISnapshotScoped
{
    private readonly List<MacAddress> _macAddresses = new();

    private Nic()
    {
        // EF Core materialization constructor.
        Name = null!;
    }

    /// <summary>Creates an observed NIC record.</summary>
    public Nic(
        Guid id,
        Guid serverId,
        Guid rackId,
        Guid snapshotId,
        string name,
        MacAddressValue macPrimary,
        LinkState? linkState = null)
    {
        Id = id;
        ServerId = serverId;
        RackId = rackId;
        SnapshotId = snapshotId;
        Name = name;
        MacPrimary = macPrimary;
        LinkState = linkState;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning server.</summary>
    public Guid ServerId { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>Observed interface name.</summary>
    public string Name { get; private set; }

    /// <summary>The normalized primary MAC address (indexed for correlation).</summary>
    public MacAddressValue MacPrimary { get; private set; }

    /// <summary>Observed link state, if known.</summary>
    public LinkState? LinkState { get; private set; }

    /// <summary>MAC addresses observed for this NIC.</summary>
    public IReadOnlyCollection<MacAddress> MacAddresses => _macAddresses;

    /// <summary>Adds an observed MAC address to this NIC.</summary>
    public void AddMacAddress(MacAddress macAddress) => _macAddresses.Add(macAddress);
}
