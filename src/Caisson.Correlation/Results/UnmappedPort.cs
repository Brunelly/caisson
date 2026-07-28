using Caisson.Domain.Enums;

namespace Caisson.Correlation.Results;

/// <summary>
/// A switch port that carried a correlation-relevant signal but could not be linked to any known NIC —
/// e.g. it learned a MAC that no BMC owns (<see cref="ReasonCode.NotSeenInBmc"/>) or it has an LLDP
/// neighbour that maps to no NIC (<see cref="ReasonCode.PortNeighbourUnknown"/>). Trunk/uplink and fully
/// idle ports are intentionally excluded as noise (see ADR 0010).
/// </summary>
/// <param name="SwitchId">The switch the port belongs to.</param>
/// <param name="PortName">The port's name.</param>
/// <param name="ReasonCodes">The reason codes explaining why the port is unmapped (always non-empty).</param>
public sealed record UnmappedPort(
    string SwitchId,
    string PortName,
    IReadOnlyList<ReasonCode> ReasonCodes);
