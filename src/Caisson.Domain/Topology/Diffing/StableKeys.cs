using System.Globalization;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Domain.Topology.Diffing;

/// <summary>
/// The canonical stable-key definitions for observed entities (the story's answered question), used
/// <b>identically</b> by the persistence mapper, the diff calculator and the read-query layer so keys
/// never drift between write and read. A stable key is derived only from natural observed attributes —
/// never from a per-snapshot <c>Guid</c> — so the same real-world entity keeps its identity across
/// snapshots and can be diffed and history-queried (AC2).
/// </summary>
/// <remarks>
/// Pure and persistence-ignorant (no EF/DbContext dependency). Keys:
/// <list type="bullet">
/// <item><description>Switch = serial, else management IP.</description></item>
/// <item><description>SwitchPort = <c>{switchKey}|{portName}</c>.</description></item>
/// <item><description>Server = BMC UUID, else hostname, else BMC address (the always-present fallback;
/// the domain <c>Server</c> does not persist a chassis serial).</description></item>
/// <item><description>NIC = normalized MAC.</description></item>
/// <item><description>VLAN = VLAN id.</description></item>
/// <item><description>MAC = <c>{normalizedMac}|{source}</c>.</description></item>
/// <item><description>LLDP = <c>{chassisId}|{portId}</c>.</description></item>
/// </list>
/// </remarks>
public static class StableKeys
{
    /// <summary>Stable key for a switch: serial when present, otherwise management IP.</summary>
    /// <exception cref="ArgumentException">Thrown when neither identifier is available.</exception>
    public static string ForSwitch(string? serial, string? managementIp)
        => Coalesce(nameof(Switch), serial, managementIp);

    /// <summary>Stable key for a persisted <see cref="Switch"/>.</summary>
    public static string ForSwitch(Switch @switch)
    {
        ArgumentNullException.ThrowIfNull(@switch);
        return ForSwitch(@switch.Serial, @switch.ManagementIp);
    }

    /// <summary>Stable key for a switch port: <c>{switchKey}|{portName}</c>.</summary>
    public static string ForSwitchPort(string switchKey, string portName)
    {
        ArgumentException.ThrowIfNullOrEmpty(switchKey);
        ArgumentException.ThrowIfNullOrEmpty(portName);
        return switchKey + "|" + portName;
    }

    /// <summary>Stable key for a persisted <see cref="SwitchPort"/> given its owning switch's key.</summary>
    public static string ForSwitchPort(string switchKey, SwitchPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return ForSwitchPort(switchKey, port.PortName);
    }

    /// <summary>Stable key for a server: BMC UUID, else hostname, else BMC address.</summary>
    public static string ForServer(string? bmcUuid, string? hostname, string? bmcAddress)
        => Coalesce(nameof(Server), bmcUuid, hostname, bmcAddress);

    /// <summary>Stable key for a persisted <see cref="Server"/>.</summary>
    public static string ForServer(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return ForServer(server.BmcUuid, server.Hostname, server.BmcAddress);
    }

    /// <summary>Stable key for a NIC: its normalized MAC.</summary>
    public static string ForNic(MacAddressValue mac) => mac.Value;

    /// <summary>Stable key for a persisted <see cref="Nic"/>.</summary>
    public static string ForNic(Nic nic)
    {
        ArgumentNullException.ThrowIfNull(nic);
        return ForNic(nic.MacPrimary);
    }

    /// <summary>Stable key for a VLAN: its 802.1Q id.</summary>
    public static string ForVlan(int vlanId) => vlanId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable key for a persisted <see cref="Vlan"/>.</summary>
    public static string ForVlan(Vlan vlan)
    {
        ArgumentNullException.ThrowIfNull(vlan);
        return ForVlan(vlan.VlanId);
    }

    /// <summary>Stable key for an observed MAC: <c>{normalizedMac}|{source}</c>.</summary>
    public static string ForMac(MacAddressValue mac, MacSource source)
        => mac.Value + "|" + source;

    /// <summary>Stable key for a persisted <see cref="MacAddress"/>.</summary>
    public static string ForMac(MacAddress mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return ForMac(mac.Mac, mac.Source);
    }

    /// <summary>Stable key for an LLDP neighbour: <c>{chassisId}|{portId}</c>.</summary>
    public static string ForLldp(string chassisId, string portId)
    {
        ArgumentException.ThrowIfNullOrEmpty(chassisId);
        ArgumentException.ThrowIfNullOrEmpty(portId);
        return chassisId + "|" + portId;
    }

    /// <summary>Stable key for a persisted <see cref="LldpNeighbour"/>.</summary>
    public static string ForLldp(LldpNeighbour neighbour)
    {
        ArgumentNullException.ThrowIfNull(neighbour);
        return ForLldp(neighbour.ChassisId, neighbour.PortId);
    }

    private static string Coalesce(string entity, params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        throw new ArgumentException(
            $"Cannot compute a stable key for a {entity}: no identifying attribute is present.");
    }
}
