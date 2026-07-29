namespace Caisson.Drivers.Simulators;

/// <summary>A committed simulator profile: which login scheme to speak, the per-command replies, and (for write-driver tests) mutable seeded state.</summary>
public sealed class RouterOsProfile
{
    /// <summary>Whether to speak the pre-6.43 MD5 challenge-response login instead of the plaintext scheme.</summary>
    public bool LegacyLogin { get; set; }

    /// <summary>
    /// Stateless fixture replay: canned rows/traps keyed by command path. This is the simulator's
    /// original mode, unchanged — existing read-driver tests keep using it exactly as before.
    /// </summary>
    public Dictionary<string, RouterOsCommandReply> Commands { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Opt-in stateful mode (AC5): when set, <see cref="RouterOsApiSimulator"/> serves
    /// <c>/interface/bridge/port/print|set</c> and <c>/interface/bridge/vlan/print</c> — plus
    /// <c>/system/scheduler/print|add|remove</c> — from this mutable state instead of the static
    /// <see cref="Commands"/> fixture replay, so the write driver's read-modify-verify cycle (and the
    /// confirmed-commit scheduler's self-revert) observe real state changes. <see cref="Commands"/> is
    /// still consulted for any command this profile does not seed state for.
    /// </summary>
    public SimulatorSwitchState? SwitchState { get; set; }
}

/// <summary>The canned reply for one command path in stateless mode: either data rows or a trap message.</summary>
public sealed class RouterOsCommandReply
{
    /// <summary>The rows to emit as <c>!re</c> replies, if this command is a query.</summary>
    public List<Dictionary<string, string>>? Rows { get; set; }

    /// <summary>A trap message to emit instead of any rows, simulating a rejected/unsupported command.</summary>
    public string? Trap { get; set; }
}
