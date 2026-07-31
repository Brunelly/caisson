using System.Globalization;
using Caisson.Correlation.Input;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Correlation;

/// <summary>A port's natural key within a snapshot: (switch, port name), compared ordinally.</summary>
internal readonly record struct PortRef(string SwitchId, string PortName);

/// <summary>The access-vs-trunk classification of a port, with the LLDP peer-switch flag that drove it.</summary>
internal readonly record struct PortClass(bool IsTrunk, bool PeerSwitchLldp);

/// <summary>
/// A single O(n) index over a <see cref="TopologyCorrelationInput"/> that gives the engine constant-time
/// lookups (MAC→sightings, port→VLANs/LLDP/learned-MAC-count, NIC-owned MACs, switch identity tokens) so
/// a rack-scale snapshot correlates in one pass (NFR3). Immutable after construction; the classification
/// cache is populated lazily but deterministically (input never changes).
/// </summary>
internal sealed class SnapshotIndex
{
    private readonly Dictionary<PortRef, SwitchPortInfo> _ports;
    private readonly Dictionary<PortRef, List<LldpNeighbourInfo>> _lldpByPort;
    private readonly Dictionary<PortRef, HashSet<MacAddressValue>> _macsByPort;
    private readonly HashSet<MacAddressValue> _ownedMacs;
    private readonly Dictionary<string, HashSet<string>> _switchIdsByToken;
    private readonly Dictionary<PortRef, PortClass> _classCache = new();

    /// <summary>Distinct (switch, port) sightings for each learned MAC.</summary>
    public IReadOnlyDictionary<MacAddressValue, List<PortRef>> SightingsByMac { get; }

    private SnapshotIndex(
        Dictionary<PortRef, SwitchPortInfo> ports,
        Dictionary<PortRef, List<LldpNeighbourInfo>> lldpByPort,
        Dictionary<PortRef, HashSet<MacAddressValue>> macsByPort,
        Dictionary<MacAddressValue, List<PortRef>> sightingsByMac,
        HashSet<MacAddressValue> ownedMacs,
        Dictionary<string, HashSet<string>> switchIdsByToken)
    {
        _ports = ports;
        _lldpByPort = lldpByPort;
        _macsByPort = macsByPort;
        SightingsByMac = sightingsByMac;
        _ownedMacs = ownedMacs;
        _switchIdsByToken = switchIdsByToken;
    }

    /// <summary>Builds the index in a single pass over the snapshot.</summary>
    public static SnapshotIndex Build(TopologyCorrelationInput input)
    {
        var ports = new Dictionary<PortRef, SwitchPortInfo>();
        var lldpByPort = new Dictionary<PortRef, List<LldpNeighbourInfo>>();
        var macsByPort = new Dictionary<PortRef, HashSet<MacAddressValue>>();
        var sightingsByMac = new Dictionary<MacAddressValue, List<PortRef>>();
        var sightingSeen = new HashSet<(MacAddressValue Mac, PortRef Port)>();
        var switchIdsByToken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var sw in input.Switches ?? [])
        {
            AddToken(switchIdsByToken, sw.SwitchId, sw.SwitchId);
            AddToken(switchIdsByToken, sw.Device?.ManagementIp, sw.SwitchId);
            AddToken(switchIdsByToken, sw.Device?.Serial, sw.SwitchId);

            foreach (var port in sw.Ports ?? [])
            {
                // Last-writer-wins is deterministic; ports are expected to be uniquely named per switch.
                ports[new PortRef(sw.SwitchId, port.PortName)] = port;
            }

            foreach (var lldp in sw.LldpNeighbours ?? [])
            {
                var portRef = new PortRef(sw.SwitchId, lldp.PortName);
                if (!lldpByPort.TryGetValue(portRef, out var list))
                {
                    list = new List<LldpNeighbourInfo>();
                    lldpByPort[portRef] = list;
                }

                list.Add(lldp);
            }

            foreach (var host in sw.BridgeHosts ?? [])
            {
                var portRef = new PortRef(sw.SwitchId, host.PortName);

                if (!macsByPort.TryGetValue(portRef, out var macSet))
                {
                    macSet = new HashSet<MacAddressValue>();
                    macsByPort[portRef] = macSet;
                }

                macSet.Add(host.Mac);

                if (sightingSeen.Add((host.Mac, portRef)))
                {
                    if (!sightingsByMac.TryGetValue(host.Mac, out var refs))
                    {
                        refs = new List<PortRef>();
                        sightingsByMac[host.Mac] = refs;
                    }

                    refs.Add(portRef);
                }
            }
        }

        var ownedMacs = new HashSet<MacAddressValue>();
        foreach (var server in input.Servers ?? [])
        {
            foreach (var nic in server.Nics ?? [])
            {
                if (nic.Mac is { } mac)
                {
                    ownedMacs.Add(mac);
                }
            }
        }

        return new SnapshotIndex(ports, lldpByPort, macsByPort, sightingsByMac, ownedMacs, switchIdsByToken);
    }

    /// <summary>The number of distinct MACs learned on a port (0 if none).</summary>
    public int LearnedMacCount(PortRef port)
        => _macsByPort.TryGetValue(port, out var macs) ? macs.Count : 0;

    /// <summary>Whether the port has any LLDP neighbour.</summary>
    public bool HasLldp(PortRef port)
        => _lldpByPort.TryGetValue(port, out var list) && list.Count > 0;

    /// <summary>Whether any MAC learned on the port is owned by a discovered BMC NIC.</summary>
    public bool PortOwnedByNic(PortRef port)
        => _macsByPort.TryGetValue(port, out var macs) && macs.Any(_ownedMacs.Contains);

    /// <summary>The distinct, ascending VLAN ids inferred for the port from its Pvid and tagged VLANs.</summary>
    public IReadOnlyList<int> VlansFor(PortRef port)
    {
        if (!_ports.TryGetValue(port, out var info))
        {
            return [];
        }

        var vlans = new SortedSet<int>();
        if (info.Pvid is { } pvid)
        {
            vlans.Add(pvid);
        }

        foreach (var tagged in info.TaggedVlans ?? [])
        {
            vlans.Add(tagged);
        }

        return vlans.Count == 0 ? [] : vlans.ToList();
    }

    /// <summary>A stable string signature of the port's VLAN configuration, for LAG comparison.</summary>
    public string VlanSignature(PortRef port)
    {
        if (!_ports.TryGetValue(port, out var info))
        {
            return "-";
        }

        var pvid = info.Pvid?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var tagged = new SortedSet<int>(info.TaggedVlans ?? []);
        return string.Concat(pvid, "|", string.Join(",", tagged));
    }

    /// <summary>Classifies a port as access or trunk using the combined, documented signals (cached).</summary>
    public PortClass Classify(PortRef port)
    {
        if (_classCache.TryGetValue(port, out var cached))
        {
            return cached;
        }

        var peerLldp = HasPeerSwitchLldp(port);
        var taggedCount = _ports.TryGetValue(port, out var info) ? (info.TaggedVlans?.Count ?? 0) : 0;
        var macCount = LearnedMacCount(port);

        // The single, shared trunk/uplink rule (LLDP peer-switch primary, multi-VLAN tagging and a high
        // learned-MAC count as fallbacks) — see docs/topology-correlation.md and ADR 0010. Reused by the
        // story-#170 rack-inventory projector via the same public classifier, so neither re-derives it.
        var isTrunk = PortRoleClassifier.IsTrunk(peerLldp, taggedCount, macCount);

        var result = new PortClass(isTrunk, peerLldp);
        _classCache[port] = result;
        return result;
    }

    private bool HasPeerSwitchLldp(PortRef port)
    {
        if (!_lldpByPort.TryGetValue(port, out var neighbours))
        {
            return false;
        }

        foreach (var neighbour in neighbours)
        {
            if (TokenIdentifiesOtherSwitch(neighbour.ChassisId, port.SwitchId)
                || TokenIdentifiesOtherSwitch(neighbour.SystemName, port.SwitchId)
                || TokenIdentifiesOtherSwitch(neighbour.MgmtAddress, port.SwitchId))
            {
                return true;
            }
        }

        return false;
    }

    private bool TokenIdentifiesOtherSwitch(string? token, string currentSwitchId)
    {
        var normalized = Normalize(token);
        return normalized is not null
            && _switchIdsByToken.TryGetValue(normalized, out var owners)
            && owners.Any(id => !string.Equals(id, currentSwitchId, StringComparison.Ordinal));
    }

    private static void AddToken(Dictionary<string, HashSet<string>> map, string? token, string switchId)
    {
        var normalized = Normalize(token);
        if (normalized is null)
        {
            return;
        }

        if (!map.TryGetValue(normalized, out var owners))
        {
            owners = new HashSet<string>(StringComparer.Ordinal);
            map[normalized] = owners;
        }

        owners.Add(switchId);
    }

    private static string? Normalize(string? token)
        => PortRoleClassifier.NormalizeToken(token);
}
