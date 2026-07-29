using Caisson.Domain.Enums;

namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// A pure, persistence-ignorant audit DTO produced on every <see cref="ISwitchMutatingDriver.SetAccessVlanAsync"/>
/// outcome — dry-run, no-op, applied, verification-failed, rolled-back, or rejected (AC6). The driver
/// assembly has no EF Core reference and never persists this itself; a caller (the future apply-API,
/// story #65) is responsible for writing it to durable storage (e.g. via
/// <c>Caisson.Domain.Topology.TopologyAuditEvent</c>/<c>IAuditEventWriter</c>). Every field is
/// deliberately typed and secret-free — never a raw command string or credential.
/// </summary>
/// <param name="CorrelationId">Correlates this record with the request/job that produced it.</param>
/// <param name="DeviceHost">The switch's host/identity as configured on the driver.</param>
/// <param name="PortName">The target port's stable interface name.</param>
/// <param name="VlanId">The requested access VLAN id.</param>
/// <param name="DryRun">Whether this record describes a dry-run (no device state changed) or a real apply attempt.</param>
/// <param name="ConfirmWindowSeconds">The confirmed-commit window that was armed (or would be armed for a dry-run), in seconds.</param>
/// <param name="Before">The observed access-VLAN state before the change, if it could be read.</param>
/// <param name="After">The observed (or, for a dry-run, intended) access-VLAN state after the change.</param>
/// <param name="ReasonCode">Why the operation ended the way it did.</param>
/// <param name="Verification">The post-change verification outcome, if an apply was attempted.</param>
/// <param name="OccurredAtUtc">When this outcome occurred, sourced from an injected <see cref="TimeProvider"/> for deterministic tests.</param>
/// <param name="ActorType">The kind of principal that requested the change.</param>
/// <param name="RequestedBy">The identifier of the actor that requested the change.</param>
public sealed record SwitchChangeAuditRecord(
    Guid CorrelationId,
    string DeviceHost,
    string PortName,
    int VlanId,
    bool DryRun,
    double ConfirmWindowSeconds,
    SwitchAccessVlanState? Before,
    SwitchAccessVlanState? After,
    SwitchChangeReasonCode ReasonCode,
    VerificationResult? Verification,
    DateTimeOffset OccurredAtUtc,
    ActorType ActorType,
    string RequestedBy);
