using Caisson.Domain.Enums;

namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// The single input to <see cref="ISwitchMutatingDriver.SetAccessVlanAsync"/> — every field is a typed
/// value, never a raw vendor command string (NFR1). The port is identified by its stable interface name
/// (e.g. <c>ether1</c>), validated via read-back and failing fast rather than guessing on ambiguity
/// (the story's answered question).
/// </summary>
/// <param name="PortName">The switch's stable interface name for the target port, e.g. <c>ether1</c>.</param>
/// <param name="DesiredVlanId">The 802.1Q access VLAN id to set. Must be validated by the driver before any I/O.</param>
/// <param name="DryRun">
/// When <c>true</c>, the driver computes and returns the intended <see cref="SwitchChangePlan"/> and a
/// before/after preview without changing device state.
/// </param>
/// <param name="ConfirmWindow">
/// The confirmed-commit window to arm before applying. When <c>null</c>, the driver factory's
/// conservative default (see <c>SwitchMutatingConnectionOptions.DefaultConfirmWindow</c>) applies.
/// </param>
/// <param name="CorrelationId">Correlates this request across logs, metrics and the audit record.</param>
/// <param name="RequestedBy">The identifier of the actor that requested the change, for audit (AC6).</param>
/// <param name="ActorType">The kind of principal that requested the change, for audit (AC6).</param>
public sealed record SetAccessVlanRequest(
    string PortName,
    int DesiredVlanId,
    bool DryRun,
    TimeSpan? ConfirmWindow,
    Guid CorrelationId,
    string RequestedBy,
    ActorType ActorType);
