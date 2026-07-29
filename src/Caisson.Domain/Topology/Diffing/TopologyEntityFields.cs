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
        => Extract(snapshot, out _);

    /// <summary>
    /// As <see cref="Extract(TopologySnapshot)"/>, additionally reporting any stable-key collision
    /// encountered. Every dictionary write uses <c>TryAdd</c> rather than an indexer assignment (finding
    /// #3): a second entity that computes the same stable key as one already seen is skipped — never
    /// silently overwriting the first — and recorded as a <see cref="StableKeyCollision"/>, mirroring the
    /// existing <see cref="StableKeys.TryForSwitchPort(string,string?,out string)"/>/
    /// <see cref="StableKeys.TryForLldp"/> skip-and-continue precedent for a missing identity.
    /// </summary>
    public static IReadOnlyDictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>
        Extract(TopologySnapshot snapshot, out IReadOnlyList<StableKeyCollision> collisions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var switches = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var ports = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var lldp = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var servers = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var nics = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var vlans = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var found = new List<StableKeyCollision>();

        foreach (var sw in snapshot.Switches)
        {
            var switchKey = StableKeys.ForSwitch(sw);
            var switchFields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["serial"] = sw.Serial,
                ["managementIp"] = sw.ManagementIp,
                ["model"] = sw.Model,
                ["osVersion"] = sw.OsVersion,
            };
            if (!switches.TryAdd(switchKey, switchFields))
            {
                found.Add(new StableKeyCollision(TopologyEntityType.Switch, switchKey));
                continue;
            }

            foreach (var port in sw.Ports)
            {
                // A port with a blank/empty name has no stable identity, so it is skipped rather than
                // aborting the diff/detail computation (and thus the all-or-nothing ingestion run, NFR3) —
                // mirroring the LLDP skip path below.
                if (!StableKeys.TryForSwitchPort(switchKey, port, out var portKey))
                {
                    continue;
                }

                var portFields = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["switch"] = switchKey,
                    ["isUp"] = port.IsUp?.ToString(CultureInfo.InvariantCulture),
                    ["pvid"] = port.Pvid?.ToString(CultureInfo.InvariantCulture),
                    ["taggedVlans"] = string.Join(",", port.TaggedVlans),
                };
                if (!ports.TryAdd(portKey, portFields))
                {
                    found.Add(new StableKeyCollision(TopologyEntityType.SwitchPort, portKey));
                    continue;
                }

                foreach (var neighbour in port.LldpNeighbours)
                {
                    // A neighbour that advertised an empty chassis/port id has no stable identity, so it is
                    // skipped rather than aborting the diff/detail computation (and thus the ingestion run).
                    if (!StableKeys.TryForLldp(neighbour, out var lldpKey))
                    {
                        continue;
                    }

                    var lldpFields = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["port"] = portKey,
                        ["systemName"] = neighbour.SystemName,
                        ["mgmtAddress"] = neighbour.MgmtAddress,
                    };
                    if (!lldp.TryAdd(lldpKey, lldpFields))
                    {
                        found.Add(new StableKeyCollision(TopologyEntityType.Lldp, lldpKey));
                    }
                }
            }
        }

        foreach (var server in snapshot.Servers)
        {
            var serverKey = StableKeys.ForServer(server);
            var serverFields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["bmcType"] = server.BmcType.ToString(),
                ["bmcAddress"] = server.BmcAddress,
                ["bmcUuid"] = server.BmcUuid,
                ["hostname"] = server.Hostname,
            };
            if (!servers.TryAdd(serverKey, serverFields))
            {
                found.Add(new StableKeyCollision(TopologyEntityType.Server, serverKey));
                continue;
            }

            foreach (var nic in server.Nics)
            {
                var nicKey = StableKeys.ForNic(nic);
                var nicFields = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["server"] = serverKey,
                    ["name"] = nic.Name,
                    ["linkState"] = nic.LinkState?.ToString(),
                };
                if (!nics.TryAdd(nicKey, nicFields))
                {
                    found.Add(new StableKeyCollision(TopologyEntityType.Nic, nicKey));
                }
            }
        }

        foreach (var vlan in snapshot.Vlans)
        {
            var vlanKey = StableKeys.ForVlan(vlan);
            if (!vlans.TryAdd(vlanKey, new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = vlan.Name }))
            {
                found.Add(new StableKeyCollision(TopologyEntityType.Vlan, vlanKey));
            }
        }

        collisions = found;
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

/// <summary>
/// Records that a second entity of <paramref name="EntityType"/> computed the same
/// <paramref name="StableKey"/> as one already seen within the same snapshot and was skipped rather than
/// silently overwriting the first (finding #3, <see cref="ReasonCode.StableKeyCollision"/>).
/// </summary>
public sealed record StableKeyCollision(TopologyEntityType EntityType, string StableKey);
