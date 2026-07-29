using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Results;

namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// The write-capable counterpart to <see cref="Caisson.Drivers.Abstractions.ReadOnly.ISwitchDiscoveryDriver"/>,
/// reserved by ADR 0006 and <c>docs/adding-a-driver.md</c> as a future <c>*Mutating</c> pair rather than
/// widening the read-only interface. Deliberately bounded to a single operation (NFR1): setting a
/// switch port's access VLAN. There is no general command-execution surface — every parameter is a
/// typed value, never a raw RouterOS (or other vendor) command string. Living in the <c>Mutating</c>
/// namespace (not <c>ReadOnly</c>) is itself part of the safety boundary: a consumer that references
/// only <see cref="Caisson.Drivers.Abstractions.ReadOnly.ISwitchDiscoveryDriver"/> can never reach this
/// interface, and the <c>ReadOnly</c> namespace's mutation-verb reflection guard
/// (<c>SafetyBoundaryGuardTests</c>) is unaffected by anything declared here.
/// </summary>
public interface ISwitchMutatingDriver
{
    /// <summary>Identity/capability metadata for this driver instance.</summary>
    DriverDescriptor Descriptor { get; }

    /// <summary>
    /// Sets (or dry-run plans) a single port's access VLAN. A real apply is wrapped in a
    /// confirmed-commit/auto-rollback window so a change that cannot be verified — or is simply never
    /// confirmed — self-reverts on the device rather than risking a persistent misconfiguration or a
    /// severed management path (see ADR 0031). Infrastructure failures (connect, auth, timeout) are
    /// returned as <see cref="DriverResult{T}.Fail"/>; every domain outcome — dry-run planned, no-op,
    /// rejected VLAN, verification failure, applied, or rolled back — is returned as
    /// <see cref="DriverResult{T}.Ok"/> carrying a <see cref="SetAccessVlanOutcome"/> with its
    /// <see cref="SwitchChangeReasonCode"/> and audit evidence, so every path is auditable.
    /// </summary>
    Task<DriverResult<SetAccessVlanOutcome>> SetAccessVlanAsync(
        SetAccessVlanRequest request, CancellationToken cancellationToken);
}
