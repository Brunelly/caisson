using Caisson.Domain.ValueObjects;

namespace Caisson.Correlation.Results;

/// <summary>
/// An ambiguous correlation of a server NIC to more than one candidate switch port (e.g. the MAC was
/// learned on multiple ports because of a LAG, a MAC flap, or a stale table entry). All candidates are
/// surfaced, ordered by descending confidence with a deterministic tie-break.
/// </summary>
/// <param name="ServerId">The server the NIC belongs to.</param>
/// <param name="NicName">The NIC's interface name.</param>
/// <param name="Mac">The NIC's normalized MAC address.</param>
/// <param name="Candidates">All candidate ports, ordered by descending confidence then port key.</param>
public sealed record AmbiguousNicMapping(
    string ServerId,
    string NicName,
    MacAddressValue Mac,
    IReadOnlyList<PortCandidate> Candidates);
