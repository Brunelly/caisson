namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// A minimal, typed observed-state subset for one port's access-VLAN configuration — the before/after
/// evidence carried on <see cref="SetAccessVlanOutcome"/> and <see cref="SwitchChangeAuditRecord"/>. Per
/// the story's answered question, verification reads back the port's PVID plus the minimal related
/// bridge/VLAN untagged-membership rows needed to assert access-VLAN semantics, not the whole bridge/VLAN
/// table.
/// </summary>
/// <param name="PortName">The port this state was observed on.</param>
/// <param name="Pvid">The port's observed native/untagged VLAN id (PVID), if known.</param>
/// <param name="UntaggedVlanIds">
/// VLAN ids on which this port is an observed untagged member (from the bridge VLAN table), read only to
/// assert access-VLAN semantics — membership mutation itself is out of scope for this story.
/// </param>
public sealed record SwitchAccessVlanState(string PortName, int? Pvid, IReadOnlyList<int> UntaggedVlanIds);
