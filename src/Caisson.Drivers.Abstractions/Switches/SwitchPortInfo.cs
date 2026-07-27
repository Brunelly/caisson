namespace Caisson.Drivers.Abstractions.Switches;

/// <summary>An observed port on a switch, as reported by a driver. Mirrors <c>Caisson.Domain.Topology.SwitchPort</c>.</summary>
/// <param name="PortName">Observed port name (natural key; the driver has no database id).</param>
/// <param name="IsUp">Observed administrative/operational up state, if known.</param>
/// <param name="Pvid">Observed port VLAN id (native/untagged), if known.</param>
/// <param name="TaggedVlans">Observed tagged VLAN ids.</param>
public sealed record SwitchPortInfo(string PortName, bool? IsUp, int? Pvid, IReadOnlyList<int> TaggedVlans);
