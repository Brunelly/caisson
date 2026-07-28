using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Correlation.Results;

/// <summary>
/// A single candidate switch port for a NIC MAC, with the bounded confidence and reason codes that
/// explain the evidence behind it. The port is identified by its natural key
/// (<paramref name="SwitchId"/>, <paramref name="PortName"/>) since the engine has no persistence
/// identity.
/// </summary>
/// <param name="SwitchId">The switch the candidate port belongs to.</param>
/// <param name="PortName">The candidate port's name.</param>
/// <param name="Confidence">The bounded confidence that the NIC is directly attached to this port.</param>
/// <param name="Vlans">The inferred VLAN ids for the port (empty when no VLAN context exists).</param>
/// <param name="ReasonCodes">The reason codes explaining the evidence for (and against) this candidate.</param>
public sealed record PortCandidate(
    string SwitchId,
    string PortName,
    ConfidenceScore Confidence,
    IReadOnlyList<int> Vlans,
    IReadOnlyList<ReasonCode> ReasonCodes);
