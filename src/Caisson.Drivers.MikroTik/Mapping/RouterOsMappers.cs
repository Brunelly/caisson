using System.Globalization;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Drivers.MikroTik.Parsing;

namespace Caisson.Drivers.MikroTik.Mapping;

/// <summary>
/// Maps raw RouterOS reply rows into the story-3 Switches info records. Each mapper is tolerant of
/// firmware variance and bad rows (AC3): a single unparseable row becomes a per-row
/// <see cref="DriverDiagnostic"/> appended to <paramref name="diagnostics"/> rather than an exception,
/// so one bad row never blocks the rest of a section.
/// </summary>
public static class RouterOsMappers
{
    private static readonly char[] ListSeparators = { ',', ' ', ';' };

    /// <summary>Maps <c>/system/resource</c> (+ optional <c>/system/routerboard</c>) into device info.</summary>
    public static SwitchDeviceInfo MapDeviceInfo(RouterOsRecord resource, RouterOsRecord? routerboard, string host)
    {
        var osVersion = resource.GetString("version");
        var model = routerboard?.GetString("model", "board-name")
            ?? resource.GetString("board-name", "platform");

        // Serial is null on CHR (a virtual machine has no RouterBOARD serial).
        var serial = routerboard?.GetString("serial-number");

        return new SwitchDeviceInfo(host, serial, model, osVersion);
    }

    /// <summary>
    /// Maps <c>/interface</c> joined with <c>/interface/bridge/port</c> (PVID) and tagged-port sets
    /// inverted from <c>/interface/bridge/vlan</c> into ports.
    /// </summary>
    public static IReadOnlyList<SwitchPortInfo> MapPorts(
        IReadOnlyList<IReadOnlyDictionary<string, string>> interfaceRows,
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgePortRows,
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgeVlanRows,
        List<DriverDiagnostic> diagnostics)
    {
        var pvidByPort = BuildPvidByPort(bridgePortRows);
        var taggedByPort = BuildTaggedVlansByPort(bridgeVlanRows);

        var ports = new List<SwitchPortInfo>(interfaceRows.Count);
        for (var i = 0; i < interfaceRows.Count; i++)
        {
            var record = new RouterOsRecord(interfaceRows[i]);
            var name = record.GetString("name");
            if (name is null)
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Error, ReasonCode.ParseError, $"interface[{i}]",
                    "Interface row has no name and was skipped."));
                continue;
            }

            // Story-3 exposes a single IsUp bool; RouterOS reports admin (disabled) and link (running)
            // separately, so we collapse them: up == administratively enabled AND link running.
            var disabled = record.GetBool("disabled");
            var running = record.GetBool("running");
            bool? isUp = disabled is null && running is null
                ? null
                : running.GetValueOrDefault() && !disabled.GetValueOrDefault();

            var tagged = taggedByPort.TryGetValue(name, out var vlans)
                ? vlans.OrderBy(v => v).ToArray()
                : Array.Empty<int>();

            pvidByPort.TryGetValue(name, out var pvid);

            ports.Add(new SwitchPortInfo(name, isUp, pvid, tagged));
        }

        return ports;
    }

    /// <summary>Maps <c>/ip/neighbor</c> into LLDP neighbours. An empty input yields an empty list, not a diagnostic.</summary>
    public static IReadOnlyList<LldpNeighbourInfo> MapLldpNeighbours(
        IReadOnlyList<IReadOnlyDictionary<string, string>> neighbourRows,
        List<DriverDiagnostic> diagnostics)
    {
        var neighbours = new List<LldpNeighbourInfo>(neighbourRows.Count);
        for (var i = 0; i < neighbourRows.Count; i++)
        {
            var record = new RouterOsRecord(neighbourRows[i]);
            var portName = FirstToken(record.GetString("interface", "on-interface", "local-interface"));
            var chassisId = record.GetString("mac-address", "chassis-id") ?? record.GetString("identity");
            if (portName is null || chassisId is null)
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, portName ?? $"neighbour[{i}]",
                    "Neighbour row lacked a local interface or chassis identity and was skipped."));
                continue;
            }

            var portId = record.GetString("interface-name", "port-id", "port") ?? string.Empty;
            var systemName = record.GetString("identity", "system-name");
            var mgmtAddress = record.GetString("address", "address4", "management-address");

            neighbours.Add(new LldpNeighbourInfo(portName, chassisId, portId, systemName, mgmtAddress));
        }

        return neighbours;
    }

    /// <summary>Maps <c>/interface/bridge/host</c> into MAC-table entries; a bad MAC becomes a per-row diagnostic.</summary>
    public static IReadOnlyList<BridgeHostEntry> MapBridgeHosts(
        IReadOnlyList<IReadOnlyDictionary<string, string>> hostRows,
        List<DriverDiagnostic> diagnostics)
    {
        var entries = new List<BridgeHostEntry>(hostRows.Count);
        for (var i = 0; i < hostRows.Count; i++)
        {
            var record = new RouterOsRecord(hostRows[i]);
            var portName = record.GetString("on-interface", "interface", "bridge");
            var rawMac = record.GetString("mac-address");

            if (portName is null || rawMac is null)
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, portName ?? $"host[{i}]",
                    "Bridge host row lacked an interface or MAC and was skipped."));
                continue;
            }

            if (!MacAddressValue.TryParse(rawMac, out var mac))
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, portName,
                    "Bridge host row had an unparseable MAC and was skipped."));
                continue;
            }

            entries.Add(new BridgeHostEntry(portName, mac));
        }

        return entries;
    }

    /// <summary>Maps the union of <c>/interface/bridge/vlan</c> and <c>/interface/vlan</c> into VLANs, deduped by id.</summary>
    public static IReadOnlyList<VlanInfo> MapVlans(
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgeVlanRows,
        IReadOnlyList<IReadOnlyDictionary<string, string>> vlanInterfaceRows,
        List<DriverDiagnostic> diagnostics)
    {
        var byId = new Dictionary<int, VlanInfo>();

        foreach (var row in bridgeVlanRows)
        {
            var record = new RouterOsRecord(row);
            foreach (var vlanId in ParseVlanIds(record.GetString("vlan-ids", "vlan-id")))
            {
                byId.TryAdd(vlanId, new VlanInfo(vlanId));
            }
        }

        foreach (var row in vlanInterfaceRows)
        {
            var record = new RouterOsRecord(row);
            var vlanId = record.GetInt("vlan-id");
            if (vlanId is null)
            {
                continue;
            }

            var name = record.GetString("name");
            // A named VLAN interface enriches a bare bridge-VLAN id of the same number.
            byId[vlanId.Value] = new VlanInfo(vlanId.Value, name);
        }

        return byId.Values.OrderBy(v => v.VlanId).ToArray();
    }

    /// <summary>
    /// Parses RouterOS VLAN-id list syntax such as <c>"10,20,30-32"</c> into the expanded, distinct id
    /// set. Unparseable fragments are skipped rather than throwing (AC3).
    /// </summary>
    public static IEnumerable<int> ParseVlanIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var seen = new HashSet<int>();
        foreach (var part in value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                if (TryInt(part[..dash], out var from) && TryInt(part[(dash + 1)..], out var to) && from <= to)
                {
                    for (var id = from; id <= to; id++)
                    {
                        if (seen.Add(id))
                        {
                            yield return id;
                        }
                    }
                }

                continue;
            }

            if (TryInt(part, out var single) && seen.Add(single))
            {
                yield return single;
            }
        }
    }

    private static Dictionary<string, int> BuildPvidByPort(
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgePortRows)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in bridgePortRows)
        {
            var record = new RouterOsRecord(row);
            var iface = record.GetString("interface");
            var pvid = record.GetInt("pvid");
            if (iface is not null && pvid is not null)
            {
                map[iface] = pvid.Value;
            }
        }

        return map;
    }

    private static Dictionary<string, HashSet<int>> BuildTaggedVlansByPort(
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgeVlanRows)
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var row in bridgeVlanRows)
        {
            var record = new RouterOsRecord(row);
            var vlanIds = ParseVlanIds(record.GetString("vlan-ids", "vlan-id")).ToArray();
            if (vlanIds.Length == 0)
            {
                continue;
            }

            var tagged = record.GetString("tagged", "tagged-ports");
            if (tagged is null)
            {
                continue;
            }

            foreach (var port in tagged.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!map.TryGetValue(port, out var set))
                {
                    set = new HashSet<int>();
                    map[port] = set;
                }

                foreach (var id in vlanIds)
                {
                    set.Add(id);
                }
            }
        }

        return map;
    }

    private static string? FirstToken(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var token = value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return token.Length > 0 ? token[0] : null;
    }

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
