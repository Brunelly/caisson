namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// The pre-computed role of a discovered switch port, carried onto the inventory by the Infrastructure
/// projector (story #170, Step 2) so the pure Domain safety rule reads a role rather than re-deriving a
/// heuristic. Access/Uplink/Unknown come from the shared <c>Caisson.Correlation.PortRoleClassifier</c>
/// trunk rule; Management is composed on top of it.
/// </summary>
public enum PortRole
{
    /// <summary>An edge/access port (the default for a port carrying a single host).</summary>
    Access,

    /// <summary>A trunk/uplink port — an LLDP peer-switch, multi-VLAN tag, or high learned-MAC count.</summary>
    Uplink,

    /// <summary>A management port — carries the switch's management IP or a reserved management port name.</summary>
    Management,

    /// <summary>Role could not be determined from the observed signals.</summary>
    Unknown,
}

/// <summary>One LLDP neighbour observed on an inventory port, carried through for reason/traceability.</summary>
/// <param name="ChassisId">The neighbour's chassis id.</param>
/// <param name="PortId">The neighbour's port id.</param>
/// <param name="SystemName">The neighbour's system name, if advertised.</param>
/// <param name="MgmtAddress">The neighbour's management address, if advertised.</param>
public sealed record InventoryLldpNeighbour(
    string ChassisId,
    string? PortId,
    string? SystemName,
    string? MgmtAddress);

/// <summary>
/// One discovered switch port in the rack inventory (story #170). A pure value carrier the projector fills
/// from the persisted snapshot; <see cref="Role"/> and <see cref="RoleReason"/> are pre-computed so the
/// Domain safety rule stays free of classification heuristics.
/// </summary>
/// <param name="StableKey">The port's stable key (<c>StableKeys.ForSwitchPort</c>).</param>
/// <param name="PortName">The observed port name (the natural key within a switch).</param>
/// <param name="TaggedVlans">The port's tagged VLANs (ascending, distinct).</param>
/// <param name="Pvid">The port's native/untagged VLAN, or null.</param>
/// <param name="IsUp">The observed link state, or null when unknown.</param>
/// <param name="Lldp">The port's observed LLDP neighbours.</param>
/// <param name="Role">The pre-computed port role.</param>
/// <param name="RoleReason">A short, heuristic-derived label explaining <see cref="Role"/> (null for Access/Unknown).</param>
public sealed record InventoryPort(
    string StableKey,
    string PortName,
    IReadOnlyList<int> TaggedVlans,
    int? Pvid,
    bool? IsUp,
    IReadOnlyList<InventoryLldpNeighbour> Lldp,
    PortRole Role,
    string? RoleReason);

/// <summary>One discovered switch in the rack inventory (story #170).</summary>
/// <param name="StableKey">The switch's stable key (<c>StableKeys.ForSwitch</c>) — the key a port intent references.</param>
/// <param name="Ports">The switch's discovered ports.</param>
public sealed record InventorySwitch(string StableKey, IReadOnlyList<InventoryPort> Ports)
{
    /// <summary>Resolves a port by exact (ordinal) port name, or null when absent.</summary>
    public InventoryPort? FindPort(string portName)
        => Ports.FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.Ordinal));
}

/// <summary>
/// The observed switch/port inventory for a rack, projected from its latest topology snapshot (story #170,
/// Step 2). Pure and EF-free so the Domain <c>PreflightValidator</c> can resolve port intents against it.
/// An <see cref="Empty"/> inventory (no completed snapshot) is a first-class, actionable state — port
/// resolution reports a blocking "refresh topology" error rather than a 500.
/// </summary>
/// <param name="RackId">The rack this inventory describes.</param>
/// <param name="SnapshotId">The snapshot the inventory was projected from, or null when none exists.</param>
/// <param name="Switches">The discovered switches (each with its ports), keyed by stable key.</param>
public sealed record RackInventory(
    Guid RackId,
    Guid? SnapshotId,
    IReadOnlyList<InventorySwitch> Switches)
{
    /// <summary>An empty inventory for a rack with no completed topology snapshot.</summary>
    public static RackInventory Empty(Guid rackId) => new(rackId, null, Array.Empty<InventorySwitch>());

    /// <summary>Whether a topology snapshot backs this inventory (false ⇒ resolution must report a refresh error).</summary>
    public bool HasSnapshot => SnapshotId is not null;

    /// <summary>Resolves a switch by exact (ordinal) stable key, or null when absent.</summary>
    public InventorySwitch? FindSwitch(string stableKey)
        => Switches.FirstOrDefault(s => string.Equals(s.StableKey, stableKey, StringComparison.Ordinal));
}
