using System.Collections.Frozen;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The complete, code-reviewable set of RouterOS API command paths this driver is permitted to send
/// (NFR1/AC1). Every entry is a <c>.../print</c> read command — there are no write, set, add, remove,
/// reboot or power verbs. <see cref="RouterOsApiClient.SendCommandAsync"/> rejects anything not in
/// <see cref="Allowlist"/> before any socket I/O, so the read-only safety boundary is enforced in the
/// transport itself, not merely by the driver's public surface.
/// </summary>
public static class RouterOsReadCommands
{
    /// <summary>Board/OS identity, version and serial (feeds device info).</summary>
    public const string SystemResource = "/system/resource/print";

    /// <summary>RouterBOARD model/firmware detail (feeds device info; absent on CHR).</summary>
    public const string SystemRouterboard = "/system/routerboard/print";

    /// <summary>All interfaces with admin/running state (feeds ports).</summary>
    public const string Interfaces = "/interface/print";

    /// <summary>Ethernet-specific interface detail (feeds ports).</summary>
    public const string EthernetInterfaces = "/interface/ethernet/print";

    /// <summary>Discovered neighbours — LLDP/CDP/MNDP (feeds LLDP neighbours).</summary>
    public const string IpNeighbors = "/ip/neighbor/print";

    /// <summary>Bridge MAC-learning host table (feeds the bridge host table).</summary>
    public const string BridgeHosts = "/interface/bridge/host/print";

    /// <summary>Per-bridge VLAN table with tagged/untagged port sets (feeds VLANs and port tags).</summary>
    public const string BridgeVlans = "/interface/bridge/vlan/print";

    /// <summary>Bridge port membership incl. PVID (feeds port native VLANs).</summary>
    public const string BridgePorts = "/interface/bridge/port/print";

    /// <summary>802.1Q VLAN interfaces (feeds VLANs on non-bridge-VLAN configs).</summary>
    public const string VlanInterfaces = "/interface/vlan/print";

    /// <summary>
    /// The immutable read-only allowlist. Ordinal string set so membership checks are exact and
    /// culture-independent.
    /// </summary>
    public static readonly FrozenSet<string> Allowlist = new[]
    {
        SystemResource,
        SystemRouterboard,
        Interfaces,
        EthernetInterfaces,
        IpNeighbors,
        BridgeHosts,
        BridgeVlans,
        BridgePorts,
        VlanInterfaces,
    }.ToFrozenSet(StringComparer.Ordinal);
}
