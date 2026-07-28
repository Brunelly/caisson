using Caisson.Domain.ValueObjects;

namespace Caisson.Correlation.Results;

/// <summary>
/// A confident 1:1 correlation of a server NIC to a single switch port. Emitted when the NIC's MAC
/// resolves to exactly one candidate port (see <see cref="AmbiguousNicMapping"/> for the &gt;1 case).
/// </summary>
/// <param name="ServerId">The server the NIC belongs to.</param>
/// <param name="NicName">The NIC's interface name.</param>
/// <param name="Mac">The NIC's normalized MAC address.</param>
/// <param name="Port">The single resolved candidate port, with its confidence and reason codes.</param>
public sealed record NicPortMapping(
    string ServerId,
    string NicName,
    MacAddressValue Mac,
    PortCandidate Port);
