using Caisson.Correlation.Results;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;

namespace Caisson.Infrastructure.Persistence.Shaping;

/// <summary>
/// Pure, DB-free projection of a loaded snapshot graph into the read model the API serves (AC3): for
/// each server NIC, its best (highest-confidence) switch-port attachment plus every candidate, each
/// carrying confidence, band and reason code and the port's VLANs; and, via an anti-join, the ports
/// that no NIC mapped to (unmapped ports). No <c>DbContext</c> dependency — it operates on an
/// already-materialised <see cref="TopologySnapshot"/> so it is trivially unit-testable.
/// </summary>
public static class TopologyGraphProjector
{
    /// <summary>Projects a materialised snapshot graph into the API read model.</summary>
    public static TopologyGraphView Project(TopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var portById = snapshot.Switches
            .SelectMany(s => s.Ports.Select(p => (Port: p, Switch: s)))
            .ToDictionary(x => x.Port.Id);

        var candidatesByNic = snapshot.CandidateMappings
            .GroupBy(m => m.NicId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Confidence.Value).ToList());

        var mappedPortIds = snapshot.CandidateMappings
            .Where(m => m.SwitchPortId is not null)
            .Select(m => m.SwitchPortId!.Value)
            .ToHashSet();

        var servers = snapshot.Servers
            .OrderBy(s => StableKeys.ForServer(s), StringComparer.Ordinal)
            .Select(server => new ServerNode(
                StableKeys.ForServer(server),
                server.Hostname,
                server.BmcUuid,
                server.Nics
                    .OrderBy(n => n.Name, StringComparer.Ordinal)
                    .Select(nic => ProjectNic(nic, candidatesByNic, portById))
                    .ToList()))
            .ToList();

        var unmappedPorts = portById.Values
            .Where(x => !mappedPortIds.Contains(x.Port.Id))
            .Select(x => new UnmappedPortNode(StableKeys.ForSwitch(x.Switch), x.Switch.Serial, x.Port.PortName))
            .OrderBy(p => p.SwitchStableKey, StringComparer.Ordinal)
            .ThenBy(p => p.PortName, StringComparer.Ordinal)
            .ToList();

        var switches = ProjectSwitches(snapshot);

        return new TopologyGraphView(
            snapshot.Id, snapshot.Version, snapshot.CorrelationId, servers, unmappedPorts, switches);
    }

    /// <summary>
    /// Story #168: a flat switch → ports inventory, additive alongside the NIC-rooted graph above — the
    /// Port Intent authoring screen needs "every discovered port on every switch", which the NIC-centric
    /// <see cref="ServerNode"/>/<see cref="UnmappedPortNode"/> shapes don't expose directly (a port with a
    /// mapped NIC never appears in <c>UnmappedPorts</c>, and a NIC-less port lookup would mean joining
    /// both lists). Computed from the same already-loaded <c>snapshot.Switches[].Ports[]</c> this method
    /// already reads above, so this adds no extra query.
    /// </summary>
    private static List<SwitchInventoryNode> ProjectSwitches(TopologySnapshot snapshot)
        => snapshot.Switches
            .OrderBy(s => StableKeys.ForSwitch(s), StringComparer.Ordinal)
            .Select(ProjectSwitchInventory)
            .ToList();

    private static SwitchInventoryNode ProjectSwitchInventory(Switch @switch)
    {
        var switchKey = StableKeys.ForSwitch(@switch);
        var ports = @switch.Ports
            .OrderBy(p => p.PortName, StringComparer.Ordinal)
            .Select(p => new SwitchPortInventoryNode(StableKeys.ForSwitchPort(switchKey, p), p.PortName))
            .ToList();
        return new SwitchInventoryNode(switchKey, @switch.Serial, @switch.ExternalDeviceKey, ports);
    }

    private static NicNode ProjectNic(
        Nic nic,
        IReadOnlyDictionary<Guid, List<TopologyCandidateMapping>> candidatesByNic,
        IReadOnlyDictionary<Guid, (SwitchPort Port, Switch Switch)> portById)
    {
        var attachments = new List<PortAttachment>();
        string? unmappedReasonCode = null;
        if (candidatesByNic.TryGetValue(nic.Id, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (candidate.SwitchPortId is { } portId && portById.TryGetValue(portId, out var located))
                {
                    attachments.Add(ProjectAttachment(candidate, located.Port, located.Switch));
                }
                else if (candidate.SwitchPortId is null)
                {
                    unmappedReasonCode ??= candidate.ReasonCode.ToString();
                }
            }
        }

        return new NicNode(
            StableKeys.ForNic(nic),
            nic.Name,
            nic.MacPrimary.ToDisplay(),
            attachments.Count > 0 ? attachments[0] : null,
            attachments,
            attachments.Count > 0 ? null : unmappedReasonCode);
    }

    private static PortAttachment ProjectAttachment(TopologyCandidateMapping candidate, SwitchPort port, Switch @switch)
    {
        var vlans = new List<int>();
        if (port.Pvid is { } pvid)
        {
            vlans.Add(pvid);
        }

        vlans.AddRange(port.TaggedVlans);

        return new PortAttachment(
            StableKeys.ForSwitch(@switch),
            @switch.Serial,
            port.PortName,
            candidate.Confidence.Value,
            ConfidenceBands.Of(candidate.Confidence).ToString(),
            candidate.ReasonCode.ToString(),
            vlans.Distinct().OrderBy(v => v).ToList());
    }
}

/// <summary>The projected topology graph for a snapshot (AC3 read model).</summary>
public sealed record TopologyGraphView(
    Guid SnapshotId,
    int Version,
    Guid CorrelationId,
    IReadOnlyList<ServerNode> Servers,
    IReadOnlyList<UnmappedPortNode> UnmappedPorts,
    IReadOnlyList<SwitchInventoryNode> Switches);

/// <summary>A server and its NICs in the projected graph.</summary>
public sealed record ServerNode(
    string StableKey,
    string? Hostname,
    string? BmcUuid,
    IReadOnlyList<NicNode> Nics);

/// <summary>
/// A NIC, its best attachment, and all candidate attachments. <see cref="UnmappedReasonCode"/> is set
/// only when the NIC has no attachment, from the NIC's null-<c>SwitchPortId</c> candidate row.
/// </summary>
public sealed record NicNode(
    string StableKey,
    string Name,
    string Mac,
    PortAttachment? BestAttachment,
    IReadOnlyList<PortAttachment> Candidates,
    string? UnmappedReasonCode = null);

/// <summary>A candidate NIC-to-port attachment with confidence, band, reason and VLANs.</summary>
public sealed record PortAttachment(
    string SwitchStableKey,
    string? SwitchSerial,
    string PortName,
    double Confidence,
    string Band,
    string ReasonCode,
    IReadOnlyList<int> Vlans);

/// <summary>A switch port that no NIC mapped to (surfaced via anti-join).</summary>
public sealed record UnmappedPortNode(
    string SwitchStableKey,
    string? SwitchSerial,
    string PortName);

/// <summary>
/// A discovered switch and its full flat port inventory (story #168) — additive alongside the NIC-rooted
/// graph, driving the Port Intent authoring screen's switch/port selection.
/// </summary>
public sealed record SwitchInventoryNode(
    string StableKey,
    string? Serial,
    string Name,
    IReadOnlyList<SwitchPortInventoryNode> Ports);

/// <summary>A discovered port within a <see cref="SwitchInventoryNode"/>'s flat inventory.</summary>
public sealed record SwitchPortInventoryNode(string StableKey, string PortName);
