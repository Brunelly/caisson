namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// Why a <see cref="ISwitchMutatingDriver.SetAccessVlanAsync"/> call ended the way it did. Deliberately a
/// new, separate enum rather than an extension of <see cref="Caisson.Domain.Enums.ReasonCode"/>: that
/// enum's doc comment scopes it to per-item correlation ambiguity recorded during discovery, not the
/// outcome of a write operation (see ADR 0031). Append-only, like <see cref="Caisson.Domain.Enums.ReasonCode"/>.
/// </summary>
public enum SwitchChangeReasonCode
{
    /// <summary>No reason recorded (should not normally be observed on a completed outcome).</summary>
    Unknown = 0,

    /// <summary>A dry-run computed and returned the intended plan; no device state changed.</summary>
    DryRunPlanned,

    /// <summary>The port's access VLAN already equalled the desired value; nothing was changed.</summary>
    NoOpAlreadyDesiredState,

    /// <summary>The change was applied and confirmed within the confirm window.</summary>
    Applied,

    /// <summary>The change was applied but not confirmed in time; the device (or simulator) reverted it automatically.</summary>
    AutoRolledBack,

    /// <summary>The change was applied but a post-change read-back did not match the expected state, so it was not confirmed.</summary>
    VerificationFailed,

    /// <summary>The requested VLAN id was outside the valid 802.1Q range (1-4094). No device I/O was performed.</summary>
    InvalidVlanId,

    /// <summary>The requested VLAN is not configured on the switch's bridge VLAN table.</summary>
    VlanNotConfigured,

    /// <summary>No port matching the requested interface name was found on the switch.</summary>
    PortNotFound,

    /// <summary>More than one port matched the requested interface name; the driver refused to guess.</summary>
    AmbiguousPort,
}
