using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Domain.Topology;

/// <summary>
/// An observed MAC address (table <c>mac_address</c>). A MAC may be associated with a
/// <see cref="Nic"/> (<see cref="NicId"/> is optional — e.g. a MAC seen only on a switch), and
/// <b>duplicate MACs within the same snapshot are allowed</b> (they represent a real observed
/// conflict, captured with a <see cref="ReasonCode"/> on a candidate mapping). The value is stored
/// normalized as a <see cref="MacAddressValue"/>.
/// </summary>
public sealed class MacAddress : ISnapshotScoped
{
    private MacAddress()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates an observed MAC record.</summary>
    public MacAddress(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        MacAddressValue mac,
        MacSource source,
        DateTime lastSeenAtUtc,
        Guid? nicId = null)
    {
        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        Mac = mac;
        Source = source;
        LastSeenAtUtc = lastSeenAtUtc;
        NicId = nicId;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning NIC, if this MAC was correlated to one.</summary>
    public Guid? NicId { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>The normalized MAC value (indexed non-uniquely: duplicates per snapshot are allowed).</summary>
    public MacAddressValue Mac { get; private set; }

    /// <summary>Where the MAC was observed (BMC or switch).</summary>
    public MacSource Source { get; private set; }

    /// <summary>When the MAC was last seen during discovery.</summary>
    public DateTime LastSeenAtUtc { get; private set; }
}
