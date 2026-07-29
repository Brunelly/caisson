namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// The outcome of re-reading device state after an apply and comparing it to the expected result (AC3).
/// A mismatch means the change must not be confirmed — the armed confirmed-commit window then
/// self-reverts it (see ADR 0031).
/// </summary>
/// <param name="Verified">Whether the observed state matched the expected state.</param>
/// <param name="ExpectedVlanId">The access VLAN id the driver expected to observe.</param>
/// <param name="ObservedVlanId">The access VLAN id actually observed, if the port could be read back.</param>
/// <param name="Detail">A human-readable explanation, e.g. naming which field mismatched.</param>
public sealed record VerificationResult(bool Verified, int ExpectedVlanId, int? ObservedVlanId, string? Detail);
