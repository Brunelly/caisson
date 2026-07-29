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
/// <item><description>VLAN = VLAN id. <b>Note:</b> the story's answered canonical-key question specified
/// <c>vlanId+switchKey</c>, but the story-2 domain <see cref="Vlan"/> carries no switch association and the
/// mapper de-duplicates VLANs per rack by id, so a rack-scoped <c>vlanId</c> is the only key derivable
/// today. The consequence — the same VLAN id observed on two switches collapses to one entity — is
/// recorded in ADR 0011; switch-scoped VLANs are deferred until the domain models the association.</description></item>
/// <item><description>MAC = <c>{normalizedMac}|{source}</c>.</description></item>
/// <item><description>LLDP = <c>{chassisId}|{portId}</c>.</description></item>
/// </list>
/// </remarks>
public static class StableKeys
{
    /// <summary>
    /// Stable key for a switch: the trusted, config-supplied device key, prefixed onto the serial (else
    /// management IP). The prefix is the sole reason two configured devices can never collide onto the
    /// same stable key even when a device reports another device's serial (finding #3) — <paramref
    /// name="deviceKey"/> must come from <c>DeviceDefinition.DeviceKey</c>, never from device-reported
    /// data.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when neither identifier is available.</exception>
    public static string ForSwitch(string deviceKey, string? serial, string? managementIp)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceKey);
        return EscapeSegment(deviceKey) + "|" + EscapeSegment(Coalesce(nameof(Switch), serial, managementIp));
    }

    /// <summary>Stable key for a persisted <see cref="Switch"/>.</summary>
    public static string ForSwitch(Switch @switch)
    {
        ArgumentNullException.ThrowIfNull(@switch);
        return ForSwitch(@switch.ExternalDeviceKey, @switch.Serial, @switch.ManagementIp);
    }

    /// <summary>
    /// Stable key for a switch port: <c>{switchKey}|{portName}</c>. <paramref name="switchKey"/> is
    /// itself an already-composed, already-escaped key (the output of <see cref="ForSwitch(Switch)"/>,
    /// carrying its own internal <c>|</c> between the device key and serial/IP) — it is appended verbatim,
    /// NOT re-escaped, so that internal separator stays a real segment boundary rather than being turned
    /// into the literal three characters <c>%7C</c>. Only <paramref name="portName"/>, the one genuinely
    /// raw/unescaped device-reported value here, is escaped.
    /// </summary>
    public static string ForSwitchPort(string switchKey, string portName)
    {
        ArgumentException.ThrowIfNullOrEmpty(switchKey);
        ArgumentException.ThrowIfNullOrEmpty(portName);
        return switchKey + "|" + EscapeSegment(portName);
    }

    /// <summary>Stable key for a persisted <see cref="SwitchPort"/> given its owning switch's key.</summary>
    public static string ForSwitchPort(string switchKey, SwitchPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return ForSwitchPort(switchKey, port.PortName);
    }

    /// <summary>
    /// Attempts to compute a switch port's stable key, returning <c>false</c> (rather than throwing) when
    /// the port name is missing/empty. A switch can report a port with a blank name (unlabelled or only
    /// partially decoded); such a port has no stable identity across snapshots and is skipped by the
    /// diff/detail layer instead of aborting the whole all-or-nothing ingestion run (NFR3), mirroring
    /// <see cref="TryForLldp"/>.
    /// </summary>
    public static bool TryForSwitchPort(string switchKey, string? portName, out string stableKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(switchKey);
        if (string.IsNullOrEmpty(portName))
        {
            stableKey = string.Empty;
            return false;
        }

        stableKey = ForSwitchPort(switchKey, portName);
        return true;
    }

    /// <summary>Attempts to compute a persisted <see cref="SwitchPort"/>'s stable key; see the string overload.</summary>
    public static bool TryForSwitchPort(string switchKey, SwitchPort port, out string stableKey)
    {
        ArgumentNullException.ThrowIfNull(port);
        return TryForSwitchPort(switchKey, port.PortName, out stableKey);
    }

    /// <summary>
    /// Stable key for a server: the trusted, config-supplied device key, prefixed onto the BMC UUID (else
    /// hostname, else BMC address). See <see cref="ForSwitch(string, string?, string?)"/> for the rationale.
    /// </summary>
    public static string ForServer(string deviceKey, string? bmcUuid, string? hostname, string? bmcAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceKey);
        return EscapeSegment(deviceKey) + "|" + EscapeSegment(Coalesce(nameof(Server), bmcUuid, hostname, bmcAddress));
    }

    /// <summary>Stable key for a persisted <see cref="Server"/>.</summary>
    public static string ForServer(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return ForServer(server.ExternalDeviceKey, server.BmcUuid, server.Hostname, server.BmcAddress);
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

    /// <summary>Stable key for an observed MAC: <c>{normalizedMac}|{source}</c>, with each segment escaped.</summary>
    public static string ForMac(MacAddressValue mac, MacSource source)
        => EscapeSegment(mac.Value) + "|" + EscapeSegment(source.ToString());

    /// <summary>Stable key for a persisted <see cref="MacAddress"/>.</summary>
    public static string ForMac(MacAddress mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return ForMac(mac.Mac, mac.Source);
    }

    /// <summary>Stable key for an LLDP neighbour: <c>{chassisId}|{portId}</c>, with each segment escaped.</summary>
    public static string ForLldp(string chassisId, string portId)
    {
        ArgumentException.ThrowIfNullOrEmpty(chassisId);
        ArgumentException.ThrowIfNullOrEmpty(portId);
        return EscapeSegment(chassisId) + "|" + EscapeSegment(portId);
    }

    /// <summary>Stable key for a persisted <see cref="LldpNeighbour"/>.</summary>
    public static string ForLldp(LldpNeighbour neighbour)
    {
        ArgumentNullException.ThrowIfNull(neighbour);
        return ForLldp(neighbour.ChassisId, neighbour.PortId);
    }

    /// <summary>
    /// Attempts to compute an LLDP neighbour's stable key, returning <c>false</c> (rather than throwing)
    /// when either identifier is missing. A device can advertise an LLDP TLV set that omits or only
    /// partially decodes the chassis/port id, leaving one empty; such a neighbour cannot be stably
    /// identified across snapshots and is skipped by the diff/detail layer instead of aborting the whole
    /// ingestion run (which is all-or-nothing, NFR3).
    /// </summary>
    public static bool TryForLldp(LldpNeighbour neighbour, out string stableKey)
    {
        ArgumentNullException.ThrowIfNull(neighbour);
        if (string.IsNullOrEmpty(neighbour.ChassisId) || string.IsNullOrEmpty(neighbour.PortId))
        {
            stableKey = string.Empty;
            return false;
        }

        stableKey = ForLldp(neighbour.ChassisId, neighbour.PortId);
        return true;
    }

    /// <summary>
    /// Percent-encodes <c>|</c> (the segment separator) and <c>%</c> (the escape character itself) in a
    /// single key segment, so a device-controlled value containing the literal separator can never make
    /// two different (component-set, values) pairs collide onto the same composite key (finding #3) — e.g.
    /// serial <c>"S1|eth0"</c> + port <c>"eth1"</c> no longer produces the same key as serial <c>"S1"</c> +
    /// port <c>"eth0|eth1"</c>. <c>%</c> must be escaped first so the two-character escape sequences
    /// themselves are unambiguous on decode (decoding is never actually performed — keys are opaque and
    /// compared only for equality — but the encoding stays reversible in principle).
    /// </summary>
    private static string EscapeSegment(string segment)
        => segment.Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

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
