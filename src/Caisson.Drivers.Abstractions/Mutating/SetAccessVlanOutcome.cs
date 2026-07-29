namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// The value carried by a successful <see cref="Caisson.Drivers.Abstractions.Results.DriverResult{T}"/>
/// from <see cref="ISwitchMutatingDriver.SetAccessVlanAsync"/>. Every domain outcome — dry-run, no-op,
/// applied, verification-failed, rolled-back, or a rejected request — is represented here rather than as
/// a <see cref="Caisson.Drivers.Abstractions.Results.DriverError"/>, so every path carries full audit
/// evidence; only infrastructure failures (connect/auth/timeout) use
/// <see cref="Caisson.Drivers.Abstractions.Results.DriverResult{T}.Fail"/> instead (see ADR 0031).
/// </summary>
/// <param name="DeviceHost">The switch's host/identity as configured on the driver.</param>
/// <param name="PortName">The target port's stable interface name.</param>
/// <param name="VlanId">The requested access VLAN id.</param>
/// <param name="CorrelationId">Correlates this outcome with the originating request.</param>
/// <param name="DryRun">Whether this outcome describes a dry-run plan or a real apply attempt.</param>
/// <param name="Plan">The ordered, typed steps that were (or would be, for a dry-run) executed.</param>
/// <param name="Before">The observed access-VLAN state before the change, if it could be read.</param>
/// <param name="After">The observed (or, for a dry-run, intended) access-VLAN state after the change.</param>
/// <param name="Verification">The post-change verification outcome, if an apply was attempted.</param>
/// <param name="Confirmed">Whether the change was explicitly confirmed (scheduler-remove sent and accepted).</param>
/// <param name="ReasonCode">Why the operation ended the way it did.</param>
/// <param name="Audit">The pure audit DTO for this outcome (not persisted by the driver itself).</param>
public sealed record SetAccessVlanOutcome(
    string DeviceHost,
    string PortName,
    int VlanId,
    Guid CorrelationId,
    bool DryRun,
    SwitchChangePlan Plan,
    SwitchAccessVlanState? Before,
    SwitchAccessVlanState? After,
    VerificationResult? Verification,
    bool Confirmed,
    SwitchChangeReasonCode ReasonCode,
    SwitchChangeAuditRecord Audit);
