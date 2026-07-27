namespace Caisson.Drivers.Abstractions.Switches;

/// <summary>
/// An observed LLDP neighbour reported on a switch port, keyed by <see cref="PortName"/> since the
/// driver has no database id to relate it to a <c>SwitchPortInfo</c>. Mirrors
/// <c>Caisson.Domain.Topology.LldpNeighbour</c>.
/// </summary>
/// <param name="PortName">The local port the neighbour was observed on.</param>
/// <param name="ChassisId">Observed LLDP chassis id of the neighbour.</param>
/// <param name="PortId">Observed LLDP port id of the neighbour.</param>
/// <param name="SystemName">Observed neighbour system name, if advertised.</param>
/// <param name="MgmtAddress">Observed neighbour management address, if advertised.</param>
public sealed record LldpNeighbourInfo(
    string PortName, string ChassisId, string PortId, string? SystemName = null, string? MgmtAddress = null);
