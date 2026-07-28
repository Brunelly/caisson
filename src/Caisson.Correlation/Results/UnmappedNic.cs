using Caisson.Domain.Enums;

namespace Caisson.Correlation.Results;

/// <summary>
/// A server NIC that could not be correlated to any switch port — e.g. its MAC was never observed in
/// any switch bridge table (<see cref="ReasonCode.NotSeenInSwitch"/>) or the BMC reported no parseable
/// MAC for it (<see cref="ReasonCode.ParseError"/>). Never silently dropped (AC4/NFR4).
/// </summary>
/// <param name="ServerId">The server the NIC belongs to.</param>
/// <param name="NicName">The NIC's interface name.</param>
/// <param name="ReasonCodes">The reason codes explaining why the NIC is unmapped (always non-empty).</param>
public sealed record UnmappedNic(
    string ServerId,
    string NicName,
    IReadOnlyList<ReasonCode> ReasonCodes);
