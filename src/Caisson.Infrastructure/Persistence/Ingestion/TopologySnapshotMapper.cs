using System.Text.Json;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;

namespace Caisson.Infrastructure.Persistence.Ingestion;

/// <summary>
/// Pure, DB-free bridge that maps the observed discovery input (story-3 info records) plus the pure
/// correlation result (story-6) into the story-2 EF domain graph (ADR 0011). Guids are minted at
/// persist time via <paramref name="newId"/> — determinism is a property of the correlation engine, not
/// the persisted snapshot — so tests can inject a deterministic factory.
/// </summary>
/// <remarks>
/// Conventions:
/// <list type="bullet">
/// <item><description>A confident <see cref="NicPortMapping"/> becomes one candidate row (with its
/// switch port set); an <see cref="AmbiguousNicMapping"/> becomes N candidate rows ordered by
/// descending confidence; an <see cref="UnmappedNic"/> becomes a candidate row with a null switch
/// port.</description></item>
/// <item><description>An <see cref="UnmappedPort"/> is persisted only as an ordinary
/// <see cref="SwitchPort"/> with no incoming candidate mapping — this keeps the NIC-anchored
/// invariant (non-nullable <c>nic_id</c>) intact; unmapped ports are surfaced by the graph query's
/// anti-join.</description></item>
/// <item><description>The primary <see cref="TopologyCandidateMapping.ReasonCode"/> is
/// <c>ReasonCodes[0]</c> — the engine does not guarantee significance ordering, so the full reason
/// list (plus VLANs and confidence band) is preserved in the bounded <c>EvidenceJson</c>.</description></item>
/// <item><description>A switch bridge-host MAC links to a known NIC when its MAC matches
/// (preserving the ADR 0002 duplicate-MAC rule), else <c>nic_id</c> is null. VLANs are de-duplicated
/// per rack by VLAN id.</description></item>
/// </list>
/// </remarks>
public static class TopologySnapshotMapper
{
    /// <summary>Maps the observed input + correlation result into a persistable snapshot graph.</summary>
    public static MappedSnapshot Map(
        Guid rackId,
        SnapshotRunContext context,
        TopologyCorrelationInput observed,
        TopologyCorrelationResult correlation,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(newId);

        var snapshotId = newId();
        var snapshot = new TopologySnapshot(
            snapshotId,
            rackId,
            context.CreatedAtUtc,
            context.CreatedBy,
            context.Source,
            context.CorrelationId,
            context.Status,
            context.SourceVersion,
            version: context.Version,
            triggerType: context.TriggerType,
            startedAtUtc: context.StartedAtUtc,
            completedAtUtc: context.CompletedAtUtc);

        var macAddresses = new List<MacAddress>();
        var portByKey = new Dictionary<(string SwitchId, string PortName), Guid>();
        var nicByServerNic = new Dictionary<(string ServerId, string NicName), Guid>();
        var nicByMac = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var seenVlans = new HashSet<int>();
        var diagnostics = new List<string>();

        MapSwitches(observed, rackId, snapshotId, context.CreatedAtUtc, newId, snapshot, macAddresses, portByKey, seenVlans, nicByMac, diagnostics);
        MapServers(observed, rackId, snapshotId, context.CreatedAtUtc, newId, snapshot, macAddresses, nicByServerNic, nicByMac, diagnostics);
        MapBridgeMacs(observed, rackId, snapshotId, context.CreatedAtUtc, newId, macAddresses, nicByMac);
        MapCandidates(correlation, rackId, snapshotId, newId, snapshot, portByKey, nicByServerNic);

        return new MappedSnapshot(snapshot, macAddresses, diagnostics);
    }

    private static void MapSwitches(
        TopologyCorrelationInput observed, Guid rackId, Guid snapshotId, DateTime lastSeen, Func<Guid> newId,
        TopologySnapshot snapshot, List<MacAddress> macAddresses,
        Dictionary<(string, string), Guid> portByKey, HashSet<int> seenVlans, Dictionary<string, Guid> nicByMac,
        List<string> diagnostics)
    {
        foreach (var s in observed.Switches)
        {
            var sw = new Switch(
                newId(), rackId, snapshotId, lastSeen,
                externalDeviceKey: s.SwitchId,
                // ManagementIp falls back to the caller-supplied stable switch id so the switch always
                // has a stable key (serial preferred). See StableKeys.
                managementIp: Truncate(s.Device?.ManagementIp ?? s.SwitchId, MaxManagementIpLength, "switch.managementIp", diagnostics),
                serial: Truncate(s.Device?.Serial, MaxSerialLength, "switch.serial", diagnostics),
                model: Truncate(s.Device?.Model, MaxModelLength, "switch.model", diagnostics),
                osVersion: Truncate(s.Device?.OsVersion, MaxOsVersionLength, "switch.osVersion", diagnostics));

            // Ports keyed by name, built once per switch — a per-neighbour FirstOrDefault scan over
            // sw.Ports would be quadratic in device-controlled port/neighbour counts (finding #13); this
            // switch is the one true quadratic that used to run inside the ingestion transaction.
            var portsByName = new Dictionary<string, SwitchPort>(StringComparer.Ordinal);

            foreach (var p in s.Ports)
            {
                var port = new SwitchPort(
                    newId(), sw.Id, rackId, snapshotId, p.PortName,
                    isUp: p.IsUp, pvid: p.Pvid, taggedVlans: p.TaggedVlans.ToArray());
                sw.AddPort(port);
                portByKey[(s.SwitchId, p.PortName)] = port.Id;
                portsByName.TryAdd(p.PortName, port);
            }

            foreach (var l in s.LldpNeighbours)
            {
                if (portsByName.TryGetValue(l.PortName, out var port))
                {
                    port.AddLldpNeighbour(new LldpNeighbour(
                        newId(), port.Id, rackId, snapshotId,
                        Truncate(l.ChassisId, MaxLldpChassisIdLength, "lldp.chassisId", diagnostics) ?? string.Empty,
                        Truncate(l.PortId, MaxLldpPortIdLength, "lldp.portId", diagnostics) ?? string.Empty,
                        Truncate(l.SystemName, MaxLldpSystemNameLength, "lldp.systemName", diagnostics),
                        Truncate(l.MgmtAddress, MaxLldpMgmtAddressLength, "lldp.mgmtAddress", diagnostics)));
                }
            }

            snapshot.AddSwitch(sw);

            foreach (var v in s.Vlans)
            {
                if (seenVlans.Add(v.VlanId))
                {
                    snapshot.AddVlan(new Vlan(newId(), rackId, snapshotId, v.VlanId, v.Name));
                }
            }
        }
    }

    private static void MapServers(
        TopologyCorrelationInput observed, Guid rackId, Guid snapshotId, DateTime lastSeen, Func<Guid> newId,
        TopologySnapshot snapshot, List<MacAddress> macAddresses,
        Dictionary<(string, string), Guid> nicByServerNic, Dictionary<string, Guid> nicByMac,
        List<string> diagnostics)
    {
        foreach (var sv in observed.Servers)
        {
            var server = new Server(
                newId(), rackId, snapshotId,
                bmcType: sv.System?.BmcType ?? BmcType.Redfish,
                bmcAddress: Truncate(sv.System?.BmcAddress ?? sv.ServerId, MaxBmcAddressLength, "server.bmcAddress", diagnostics) ?? sv.ServerId,
                externalDeviceKey: sv.ServerId,
                bmcUuid: Truncate(sv.System?.BmcUuid, MaxBmcUuidLength, "server.bmcUuid", diagnostics),
                hostname: Truncate(sv.System?.Hostname, MaxHostnameLength, "server.hostname", diagnostics));

            foreach (var n in sv.Nics)
            {
                // A MAC-less NIC cannot be represented (MacPrimary is required and is the NIC stable
                // key); the engine already surfaces it as an unmapped NIC (ParseError), so it is skipped
                // here rather than persisted without identity.
                if (n.Mac is not { } mac)
                {
                    continue;
                }

                var nic = new Nic(
                    newId(), server.Id, rackId, snapshotId,
                    Truncate(n.Name, MaxNicNameLength, "nic.name", diagnostics) ?? n.Name, mac, n.LinkState);
                server.AddNic(nic);
                nicByServerNic[(sv.ServerId, n.Name)] = nic.Id;
                if (!nicByMac.TryAdd(mac.Value, nic.Id))
                {
                    // A device reported the same MAC on more than one NIC within this snapshot — flag it
                    // rather than silently dropping the second observation (finding #3).
                    diagnostics.Add(
                        $"[{ReasonCode.DuplicateNicMac}] MAC {mac.Value} was observed on more than one NIC within the same snapshot.");
                }

                macAddresses.Add(new MacAddress(
                    newId(), rackId, snapshotId, mac, MacSource.Bmc, lastSeen, nic.Id));
            }

            snapshot.AddServer(server);
        }
    }

    // Bounds mirror the DB column limits declared in SwitchConfiguration/ServerConfiguration/
    // LldpNeighbourConfiguration/NicConfiguration — an over-long device-supplied value is truncated
    // with a diagnostic here (finding #28) rather than reaching PostgreSQL and raising SQLSTATE 22001.
    private const int MaxManagementIpLength = 64;
    private const int MaxSerialLength = 128;
    private const int MaxModelLength = 128;
    private const int MaxOsVersionLength = 128;
    private const int MaxBmcAddressLength = 128;
    private const int MaxBmcUuidLength = 128;
    private const int MaxHostnameLength = 256;
    private const int MaxNicNameLength = 128;
    private const int MaxLldpChassisIdLength = 256;
    private const int MaxLldpPortIdLength = 256;
    private const int MaxLldpSystemNameLength = 256;
    private const int MaxLldpMgmtAddressLength = 128;

    private static string? Truncate(string? value, int maxLength, string field, List<string> diagnostics)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        diagnostics.Add(
            $"[{ReasonCode.FieldTruncated}] {field} was {value.Length} characters and was truncated to {maxLength}.");
        return value[..maxLength];
    }

    private static void MapBridgeMacs(
        TopologyCorrelationInput observed, Guid rackId, Guid snapshotId, DateTime lastSeen, Func<Guid> newId,
        List<MacAddress> macAddresses, Dictionary<string, Guid> nicByMac)
    {
        foreach (var s in observed.Switches)
        {
            foreach (var b in s.BridgeHosts)
            {
                var nicId = nicByMac.TryGetValue(b.Mac.Value, out var id) ? id : (Guid?)null;
                macAddresses.Add(new MacAddress(
                    newId(), rackId, snapshotId, b.Mac, MacSource.Switch, lastSeen, nicId));
            }
        }
    }

    private static void MapCandidates(
        TopologyCorrelationResult correlation, Guid rackId, Guid snapshotId, Func<Guid> newId,
        TopologySnapshot snapshot,
        Dictionary<(string, string), Guid> portByKey, Dictionary<(string, string), Guid> nicByServerNic)
    {
        foreach (var m in correlation.Mappings)
        {
            if (nicByServerNic.TryGetValue((m.ServerId, m.NicName), out var nicId))
            {
                snapshot.AddCandidateMapping(BuildCandidate(newId(), rackId, snapshotId, nicId, m.Port, portByKey));
            }
        }

        foreach (var a in correlation.AmbiguousMappings)
        {
            if (!nicByServerNic.TryGetValue((a.ServerId, a.NicName), out var nicId))
            {
                continue;
            }

            foreach (var candidate in a.Candidates)
            {
                snapshot.AddCandidateMapping(BuildCandidate(newId(), rackId, snapshotId, nicId, candidate, portByKey));
            }
        }

        foreach (var u in correlation.UnmappedNics)
        {
            if (!nicByServerNic.TryGetValue((u.ServerId, u.NicName), out var nicId))
            {
                continue;
            }

            var reason = u.ReasonCodes.Count > 0 ? u.ReasonCodes[0] : ReasonCode.Unknown;
            var evidence = Evidence(switchId: null, portName: null, confidence: 0.0, vlans: Array.Empty<int>(), u.ReasonCodes);
            snapshot.AddCandidateMapping(new TopologyCandidateMapping(
                newId(), rackId, snapshotId, nicId, ConfidenceScore.From(0.0), reason,
                switchPortId: null, evidenceJson: evidence));
        }

        // UnmappedPorts are intentionally NOT mapped to candidate rows — they exist as ordinary
        // SwitchPort rows and are surfaced by the graph query's anti-join.
    }

    private static TopologyCandidateMapping BuildCandidate(
        Guid id, Guid rackId, Guid snapshotId, Guid nicId, PortCandidate port,
        Dictionary<(string, string), Guid> portByKey)
    {
        var switchPortId = portByKey.TryGetValue((port.SwitchId, port.PortName), out var portId)
            ? portId
            : (Guid?)null;
        var reason = port.ReasonCodes.Count > 0 ? port.ReasonCodes[0] : ReasonCode.Unknown;
        var evidence = Evidence(port.SwitchId, port.PortName, port.Confidence.Value, port.Vlans, port.ReasonCodes);

        return new TopologyCandidateMapping(
            id, rackId, snapshotId, nicId, port.Confidence, reason, switchPortId, evidence);
    }

    private static string Evidence(
        string? switchId, string? portName, double confidence, IReadOnlyList<int> vlans,
        IReadOnlyList<ReasonCode> reasonCodes)
    {
        var band = ConfidenceBands.Of(confidence).ToString();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["switchId"] = switchId,
            ["portName"] = portName,
            ["confidence"] = Math.Round(confidence, 4),
            ["band"] = band,
            ["vlans"] = vlans,
            ["reasonCodes"] = reasonCodes.Select(r => r.ToString()).ToList(),
        };

        var json = JsonSerializer.Serialize(payload);
        // Defensive: keep within the bounded jsonb column even for pathological reason lists.
        if (json.Length > TopologyCandidateMapping.MaxEvidenceJsonLength)
        {
            json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["band"] = band,
                ["confidence"] = Math.Round(confidence, 4),
                ["truncated"] = true,
            });
        }

        return json;
    }
}

/// <summary>The run/audit metadata carried onto a persisted <see cref="TopologySnapshot"/>.</summary>
/// <param name="Version">The monotonic per-rack snapshot version (assigned by the ingestion service).</param>
/// <param name="TriggerType">How the discovery run was initiated.</param>
/// <param name="CreatedBy">The service account or user that initiated the run (triggeredBy).</param>
/// <param name="Source">The source driver that produced the observations.</param>
/// <param name="SourceVersion">Optional source-driver version.</param>
/// <param name="CorrelationId">Correlation id of the discovery run.</param>
/// <param name="Status">The terminal outcome of the run.</param>
/// <param name="StartedAtUtc">When the run started.</param>
/// <param name="CompletedAtUtc">When the run completed.</param>
/// <param name="CreatedAtUtc">The snapshot creation time (primary sort key for latest selection).</param>
public sealed record SnapshotRunContext(
    int Version,
    TriggerType TriggerType,
    string CreatedBy,
    string Source,
    string? SourceVersion,
    Guid CorrelationId,
    SnapshotStatus Status,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    DateTime CreatedAtUtc);

/// <summary>
/// The persistable output of the mapper: the snapshot graph (switches/ports/lldp, servers/nics, vlans,
/// candidate mappings) plus the flat list of observed MAC rows (BMC- and switch-sourced) which are
/// inserted directly since they are not all reachable through a snapshot navigation.
/// </summary>
/// <param name="Snapshot">The snapshot graph root.</param>
/// <param name="MacAddresses">All observed MAC rows for the snapshot.</param>
/// <param name="Diagnostics">
/// Human-readable, secret-free notes recorded during mapping — an over-long field truncation or a
/// duplicate-MAC-within-snapshot observation (finding #3/#28) — folded into the discovery audit event so
/// they are visible rather than silent.
/// </param>
public sealed record MappedSnapshot(
    TopologySnapshot Snapshot,
    IReadOnlyList<MacAddress> MacAddresses,
    IReadOnlyList<string> Diagnostics);
