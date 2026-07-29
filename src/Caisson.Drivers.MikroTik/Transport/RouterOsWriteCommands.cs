using System.Collections.Frozen;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The complete, code-reviewable set of RouterOS API command paths the write path is permitted to send
/// (NFR1) — a SEPARATE allowlist from <see cref="RouterOsReadCommands.Allowlist"/>, which is never
/// widened by this story. Bounded to exactly the commands needed to read a port's bridge/VLAN state, set
/// its PVID, and arm/cancel the confirmed-commit self-revert scheduler job (ADR 0031) — nothing else
/// (no reboot, user, firewall or arbitrary script-execution command is ever on this list).
/// <see cref="RouterOsWriteApiClient.ExecuteAsync"/> rejects anything not in <see cref="Allowlist"/>
/// before any socket I/O, mirroring the read-only chokepoint in <see cref="RouterOsApiClient.SendCommandAsync"/>.
/// </summary>
public static class RouterOsWriteCommands
{
    /// <summary>Reads bridge port membership incl. PVID — used to read the before/after access-VLAN state.</summary>
    public const string BridgePortPrint = "/interface/bridge/port/print";

    /// <summary>Sets a bridge port's PVID — the one mutating command this story's write surface may send.</summary>
    public const string BridgePortSet = "/interface/bridge/port/set";

    /// <summary>Reads the per-bridge VLAN table — used to confirm the desired VLAN is configured and for verification.</summary>
    public const string BridgeVlanPrint = "/interface/bridge/vlan/print";

    /// <summary>Reads scheduler entries — used to check for a stale confirmed-commit entry before arming a new one.</summary>
    public const string SchedulerPrint = "/system/scheduler/print";

    /// <summary>Arms the confirmed-commit self-revert job before applying a change.</summary>
    public const string SchedulerAdd = "/system/scheduler/add";

    /// <summary>Cancels the armed self-revert job once a change has been verified — the "confirm" signal (AC3).</summary>
    public const string SchedulerRemove = "/system/scheduler/remove";

    /// <summary>
    /// The immutable write allowlist. Ordinal string set so membership checks are exact and
    /// culture-independent, mirroring <see cref="RouterOsReadCommands.Allowlist"/>.
    /// </summary>
    public static readonly FrozenSet<string> Allowlist = new[]
    {
        BridgePortPrint,
        BridgePortSet,
        BridgeVlanPrint,
        SchedulerPrint,
        SchedulerAdd,
        SchedulerRemove,
    }.ToFrozenSet(StringComparer.Ordinal);
}
