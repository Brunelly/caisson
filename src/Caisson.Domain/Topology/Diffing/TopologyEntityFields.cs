using System.Globalization;
using Caisson.Domain.Enums;

namespace Caisson.Domain.Topology.Diffing;

/// <summary>
/// The single, pure definition of an observed entity's comparable fields, keyed by its
/// <see cref="StableKeys"/> value. Used identically by the diff calculator (to compare two snapshots)
/// and the entity-detail query (to present the latest representation), so the field set never drifts
/// between diffing and reads. Field values are rendered to invariant strings so comparison and JSON
/// serialization are deterministic. Unchanged entities are recognised by identical field maps.
/// </summary>
/// <remarks>
/// Diffed entity types: <see cref="TopologyEntityType.Switch"/>, <see cref="TopologyEntityType.SwitchPort"/>,
/// <see cref="TopologyEntityType.Server"/>, <see cref="TopologyEntityType.Nic"/>,
/// <see cref="TopologyEntityType.Vlan"/> and <see cref="TopologyEntityType.Lldp"/>. A NIC's stable key is
/// its MAC, so NIC-level identity already captures MAC changes; standalone
/// <see cref="TopologyEntityType.Mac"/> rows (which are not part of the snapshot navigation graph) are
/// intentionally not diffed to avoid switch-learning churn noise.
/// </remarks>
public static class TopologyEntityFields
{
    /// <summary>An ordered (type, stableKey) → field-map view of every diffable entity in a snapshot.</summary>
    public static IReadOnlyDictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>
        Extract(TopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var switches = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var ports = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var lldp = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var servers = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var nics = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var vlans = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

        foreach (var sw in snapshot.Switches)
        {
            var switchKey = StableKeys.ForSwitch(sw);
            switches[switchKey] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["serial"] = sw.Serial,
                ["managementIp"] = sw.ManagementIp,
                ["model"] = sw.Model,
                ["osVersion"] = sw.OsVersion,
            };

            foreach (var port in sw.Ports)
            {
                var portKey = StableKeys.ForSwitchPort(switchKey, port);
                ports[portKey] = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["switch"] = switchKey,
                    ["isUp"] = port.IsUp?.ToString(CultureInfo.InvariantCulture),
                    ["pvid"] = port.Pvid?.ToString(CultureInfo.InvariantCulture),
                    ["taggedVlans"] = string.Join(",", port.TaggedVlans),
                };

                foreach (var neighbour in port.LldpNeighbours)
                {
                    lldp[StableKeys.ForLldp(neighbour)] = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["port"] = portKey,
                        ["systemName"] = neighbour.SystemName,
                        ["mgmtAddress"] = neighbour.MgmtAddress,
                    };
                }
            }
        }

        foreach (var server in snapshot.Servers)
        {
            var serverKey = StableKeys.ForServer(server);
            servers[serverKey] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["bmcType"] = server.BmcType.ToString(),
                ["bmcAddress"] = server.BmcAddress,
                ["bmcUuid"] = server.BmcUuid,
                ["hostname"] = server.Hostname,
            };

            foreach (var nic in server.Nics)
            {
                nics[StableKeys.ForNic(nic)] = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["server"] = serverKey,
                    ["name"] = nic.Name,
                    ["linkState"] = nic.LinkState?.ToString(),
                };
            }
        }

        foreach (var vlan in snapshot.Vlans)
        {
            vlans[StableKeys.ForVlan(vlan)] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["name"] = vlan.Name,
            };
        }

        return new Dictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>
        {
            [TopologyEntityType.Switch] = switches,
            [TopologyEntityType.SwitchPort] = ports,
            [TopologyEntityType.Lldp] = lldp,
            [TopologyEntityType.Server] = servers,
            [TopologyEntityType.Nic] = nics,
            [TopologyEntityType.Vlan] = vlans,
        };
    }
}
