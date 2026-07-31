using Caisson.Correlation;
using Caisson.Domain.Enums;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;

namespace Caisson.Infrastructure.Persistence.Shaping;

/// <summary>
/// Projects a loaded <see cref="TopologySnapshot"/> (obtained via the existing
/// <c>SnapshotQueries.LatestSnapshotWithGraphAsync</c>, which already eager-loads
/// <c>Switches → Ports → LldpNeighbours</c> — no new query) into the pure, EF-free
/// <see cref="RackInventory"/> the story-#170 <c>PreflightValidator</c> resolves port intents against.
/// Pure, DB-free and deterministic — a sibling of <see cref="TopologyGraphProjector"/>.
///
/// Port role is not stored (M0 observed-state has no role field), so it is classified here and carried
/// onto each <see cref="InventoryPort"/>: the trunk/uplink decision REUSES the shared, already-unit-tested
/// <see cref="PortRoleClassifier"/> (the same rule the correlation engine's <c>SnapshotIndex</c> uses)
/// rather than a hand-rolled heuristic, keeping the Domain layer free of classification logic. The
/// 'management' signal is composed on top (reserved management port name, or an LLDP management address
/// matching the switch's own management IP). Learned-MAC-per-port counts are not persisted in the observed
/// graph, so that fallback trunk signal is passed as zero — the LLDP peer-switch and multi-VLAN-tag signals
/// (both persisted) drive the classification.
/// </summary>
public static class RackInventoryProjector
{
    /// <summary>
    /// Builds the rack inventory from the latest snapshot. Returns <see cref="RackInventory.Empty"/> when
    /// the rack has no snapshot or its latest snapshot carries no usable observed data (Failed), so port
    /// resolution reports an actionable "refresh topology" error rather than a 500.
    /// </summary>
    public static RackInventory Project(Guid rackId, TopologySnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Status == SnapshotStatus.Failed)
        {
            return RackInventory.Empty(rackId);
        }

        var switchIdsByToken = BuildSwitchTokenMap(snapshot);

        var switches = snapshot.Switches
            .OrderBy(s => StableKeys.ForSwitch(s), StringComparer.Ordinal)
            .Select(sw => ProjectSwitch(sw, switchIdsByToken))
            .ToList();

        return new RackInventory(rackId, snapshot.Id, switches);
    }

    private static InventorySwitch ProjectSwitch(
        Switch @switch, IReadOnlyDictionary<string, HashSet<string>> switchIdsByToken)
    {
        var switchKey = StableKeys.ForSwitch(@switch);
        var mgmtIp = PortRoleClassifier.NormalizeToken(@switch.ManagementIp);

        var ports = @switch.Ports
            .OrderBy(p => p.PortName, StringComparer.Ordinal)
            .Select(p => ProjectPort(@switch, switchKey, p, mgmtIp, switchIdsByToken))
            .ToList();

        return new InventorySwitch(switchKey, ports);
    }

    private static InventoryPort ProjectPort(
        Switch @switch,
        string switchKey,
        SwitchPort port,
        string? switchMgmtIp,
        IReadOnlyDictionary<string, HashSet<string>> switchIdsByToken)
    {
        var lldp = port.LldpNeighbours
            .Select(n => new InventoryLldpNeighbour(n.ChassisId, n.PortId, n.SystemName, n.MgmtAddress))
            .ToList();

        var peerSwitchLldp = HasPeerSwitchLldp(port, @switch.ExternalDeviceKey, switchIdsByToken);
        var taggedVlans = port.TaggedVlans.Distinct().OrderBy(v => v).ToList();

        // Learned-MAC-per-port counts are not persisted in the observed graph, so the high-MAC fallback is
        // unavailable post-persistence; the (persisted) LLDP-peer-switch and multi-tag signals classify.
        var isTrunk = PortRoleClassifier.IsTrunk(peerSwitchLldp, taggedVlans.Count, learnedMacCount: 0);

        var (role, reason) = ClassifyRole(port, switchMgmtIp, peerSwitchLldp, taggedVlans.Count, isTrunk);

        return new InventoryPort(
            StableKeys.ForSwitchPort(switchKey, port),
            port.PortName,
            taggedVlans,
            port.Pvid,
            port.IsUp,
            lldp,
            role,
            reason);
    }

    /// <summary>
    /// Classifies a port's role: management first (self-lockout risk is the most important guardrail),
    /// then uplink (trunk), else access. Each non-access role carries a short heuristic-derived reason.
    /// </summary>
    private static (PortRole Role, string? Reason) ClassifyRole(
        SwitchPort port, string? switchMgmtIp, bool peerSwitchLldp, int taggedVlanCount, bool isTrunk)
    {
        if (IsReservedManagementPortName(port.PortName))
        {
            return (PortRole.Management, "reserved management port name");
        }

        if (switchMgmtIp is not null
            && port.LldpNeighbours.Any(n => PortRoleClassifier.NormalizeToken(n.MgmtAddress) == switchMgmtIp))
        {
            return (PortRole.Management, "LLDP management address matches the switch management IP");
        }

        if (isTrunk)
        {
            var reason = peerSwitchLldp
                ? "LLDP neighbour is another switch"
                : taggedVlanCount > 1
                    ? "carries multiple tagged VLANs"
                    : "high learned-MAC count";
            return (PortRole.Uplink, reason);
        }

        return (PortRole.Access, null);
    }

    /// <summary>Builds the case-folded switch-identity token map (deviceKey/serial/managementIp → owning switches).</summary>
    private static Dictionary<string, HashSet<string>> BuildSwitchTokenMap(TopologySnapshot snapshot)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var sw in snapshot.Switches)
        {
            AddToken(map, sw.ExternalDeviceKey, sw.ExternalDeviceKey);
            AddToken(map, sw.ManagementIp, sw.ExternalDeviceKey);
            AddToken(map, sw.Serial, sw.ExternalDeviceKey);
        }

        return map;
    }

    private static bool HasPeerSwitchLldp(
        SwitchPort port, string currentSwitchId, IReadOnlyDictionary<string, HashSet<string>> switchIdsByToken)
    {
        foreach (var neighbour in port.LldpNeighbours)
        {
            if (TokenIdentifiesOtherSwitch(neighbour.ChassisId, currentSwitchId, switchIdsByToken)
                || TokenIdentifiesOtherSwitch(neighbour.SystemName, currentSwitchId, switchIdsByToken)
                || TokenIdentifiesOtherSwitch(neighbour.MgmtAddress, currentSwitchId, switchIdsByToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TokenIdentifiesOtherSwitch(
        string? token, string currentSwitchId, IReadOnlyDictionary<string, HashSet<string>> switchIdsByToken)
    {
        var normalized = PortRoleClassifier.NormalizeToken(token);
        return normalized is not null
            && switchIdsByToken.TryGetValue(normalized, out var owners)
            && owners.Any(id => !string.Equals(id, currentSwitchId, StringComparison.Ordinal));
    }

    private static void AddToken(Dictionary<string, HashSet<string>> map, string? token, string switchId)
    {
        var normalized = PortRoleClassifier.NormalizeToken(token);
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

    private static bool IsReservedManagementPortName(string portName)
    {
        var normalized = portName.Trim().ToLowerInvariant();
        return normalized.Contains("mgmt", StringComparison.Ordinal)
            || normalized.Contains("management", StringComparison.Ordinal);
    }
}
