namespace Caisson.Drivers.Abstractions.Switches;

/// <summary>An observed VLAN reported by a switch driver. Mirrors <c>Caisson.Domain.Topology.Vlan</c>.</summary>
/// <param name="VlanId">The observed 802.1Q VLAN id.</param>
/// <param name="Name">Observed VLAN name, if known.</param>
public sealed record VlanInfo(int VlanId, string? Name = null);
