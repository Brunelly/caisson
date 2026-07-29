namespace Caisson.Drivers.Simulators;

/// <summary>
/// Mutable in-memory switch state backing the stateful mode of <see cref="RouterOsApiSimulator"/> (AC5):
/// port PVIDs and per-VLAN tagged/untagged port membership. Seeded once via a
/// <see cref="RouterOsProfile.SwitchState"/> and then mutated only by
/// <c>/interface/bridge/port/set</c> and the confirmed-commit scheduler's self-revert — so the write
/// driver's own read-modify-verify cycle observes its own writes, unlike the simulator's original
/// stateless fixture-replay mode (which existing read-driver tests continue to use unchanged).
/// </summary>
public sealed class SimulatorSwitchState
{
    private readonly Dictionary<string, int> _portPvid;
    private readonly Dictionary<int, SimulatorVlanMembership> _vlans;

    /// <summary>Creates the state, optionally seeded with initial port PVIDs and VLAN membership.</summary>
    public SimulatorSwitchState(
        IReadOnlyDictionary<string, int>? portPvid = null,
        IReadOnlyDictionary<int, SimulatorVlanMembership>? vlans = null)
    {
        _portPvid = portPvid is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(portPvid, StringComparer.Ordinal);
        _vlans = vlans is null
            ? new Dictionary<int, SimulatorVlanMembership>()
            : new Dictionary<int, SimulatorVlanMembership>(vlans);
    }

    /// <summary>The current PVID for every seeded port.</summary>
    public IReadOnlyDictionary<string, int> Ports => _portPvid;

    /// <summary>The current tagged/untagged membership for every seeded VLAN.</summary>
    public IReadOnlyDictionary<int, SimulatorVlanMembership> Vlans => _vlans;

    /// <summary>Reads a port's current PVID, or <c>null</c> if the port is not seeded.</summary>
    public int? GetPvid(string port) => _portPvid.TryGetValue(port, out var pvid) ? pvid : null;

    /// <summary>Whether <paramref name="port"/> is a seeded port on this switch.</summary>
    public bool HasPort(string port) => _portPvid.ContainsKey(port);

    /// <summary>Sets a port's PVID — the one mutation <c>/interface/bridge/port/set</c> and a fired rollback ever perform.</summary>
    public void SetPvid(string port, int pvid) => _portPvid[port] = pvid;
}

/// <summary>A VLAN's tagged/untagged port membership, as reported by <c>/interface/bridge/vlan/print</c>.</summary>
/// <param name="Tagged">Ports on which this VLAN is tagged.</param>
/// <param name="Untagged">Ports on which this VLAN is the untagged (access) VLAN.</param>
public sealed record SimulatorVlanMembership(IReadOnlyList<string> Tagged, IReadOnlyList<string> Untagged);
