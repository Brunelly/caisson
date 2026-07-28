using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Correlation.Input;

/// <summary>
/// A read-only snapshot of one switch's discovery output, assembled from the story-3
/// <c>ISwitchDiscoveryDriver</c> info records. The engine keys the switch by <paramref name="SwitchId"/>
/// (a caller-supplied stable identifier) since the driver records carry no persistence identity.
/// </summary>
/// <param name="SwitchId">Caller-supplied stable identifier for the switch.</param>
/// <param name="Device">Observed switch identity/version, if known.</param>
/// <param name="Ports">Observed ports on the switch.</param>
/// <param name="LldpNeighbours">Observed LLDP neighbours, keyed by local port name.</param>
/// <param name="BridgeHosts">Observed bridge/MAC-learning host-table entries.</param>
/// <param name="Vlans">Observed VLANs configured on the switch.</param>
public sealed record SwitchTopologySnapshot(
    string SwitchId,
    SwitchDeviceInfo? Device,
    IReadOnlyList<SwitchPortInfo> Ports,
    IReadOnlyList<LldpNeighbourInfo> LldpNeighbours,
    IReadOnlyList<BridgeHostEntry> BridgeHosts,
    IReadOnlyList<VlanInfo> Vlans);
