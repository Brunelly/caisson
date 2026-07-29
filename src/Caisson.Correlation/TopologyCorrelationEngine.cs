using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Correlation;

/// <summary>
/// The default <see cref="ITopologyCorrelationEngine"/>: a pure, stateless, deterministic function from a
/// discovery snapshot to an explainable NIC↔port↔VLAN mapping. It performs no I/O, reads no clock and
/// uses no randomness, and never depends on hash/dictionary enumeration order — every returned collection
/// is explicitly sorted (NFR1/NFR2). See docs/topology-correlation.md and ADR 0010 for the scoring model,
/// the access-vs-trunk and LAG heuristics, and the tie-break rules.
/// </summary>
internal sealed class TopologyCorrelationEngine : ITopologyCorrelationEngine
{
    /// <summary>
    /// Upper bound on the ranked candidates <see cref="ResolveAmbiguous"/> returns for one ambiguous NIC
    /// (finding #11) — when one MAC is learned on N ports, this bounds N (and, downstream, the
    /// <c>topology_candidate_mapping</c> rows persisted for that NIC) to the top-K by score, mirroring the
    /// default in <c>Caisson.Orchestration.Options.DiscoveryOrchestrationOptions.MaxCandidatesPerNic</c>.
    /// Fixed here (not configurable) to keep this project's zero-config, pure/AOT contract (ADR 0010).
    /// </summary>
    private const int MaxCandidatesPerNic = 16;

    /// <inheritdoc />
    public TopologyCorrelationResult Correlate(TopologyCorrelationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var index = SnapshotIndex.Build(input);

        var mappings = new List<NicPortMapping>();
        var ambiguous = new List<AmbiguousNicMapping>();
        var unmappedNics = new List<UnmappedNic>();

        foreach (var server in input.Servers ?? [])
        {
            foreach (var nic in server.Nics ?? [])
            {
                CorrelateNic(server.ServerId, nic.Name, nic.Mac, index, mappings, ambiguous, unmappedNics);
            }
        }

        var unmappedPorts = CollectUnmappedPorts(input, index);

        return new TopologyCorrelationResult(
            mappings
                .OrderBy(m => m.ServerId, StringComparer.Ordinal)
                .ThenBy(m => m.NicName, StringComparer.Ordinal)
                .ToList(),
            ambiguous
                .OrderBy(a => a.ServerId, StringComparer.Ordinal)
                .ThenBy(a => a.NicName, StringComparer.Ordinal)
                .ToList(),
            unmappedNics
                .OrderBy(u => u.ServerId, StringComparer.Ordinal)
                .ThenBy(u => u.NicName, StringComparer.Ordinal)
                .ToList(),
            unmappedPorts
                .OrderBy(u => u.SwitchId, StringComparer.Ordinal)
                .ThenBy(u => u.PortName, StringComparer.Ordinal)
                .ToList());
    }

    private static void CorrelateNic(
        string serverId,
        string nicName,
        MacAddressValue? mac,
        SnapshotIndex index,
        List<NicPortMapping> mappings,
        List<AmbiguousNicMapping> ambiguous,
        List<UnmappedNic> unmappedNics)
    {
        if (mac is not { } nicMac)
        {
            // The BMC reported the interface but not a parseable MAC — visible, never dropped (AC4).
            unmappedNics.Add(new UnmappedNic(serverId, nicName, [ReasonCode.ParseError]));
            return;
        }

        if (!index.SightingsByMac.TryGetValue(nicMac, out var sightings) || sightings.Count == 0)
        {
            unmappedNics.Add(new UnmappedNic(serverId, nicName, [ReasonCode.NotSeenInSwitch]));
            return;
        }

        var accessCandidates = new List<PortCandidate>();
        var trunkCandidates = new List<PortCandidate>();
        foreach (var sighting in sightings)
        {
            var portClass = index.Classify(sighting);
            var candidate = BuildCandidate(sighting, portClass, index);
            (portClass.IsTrunk ? trunkCandidates : accessCandidates).Add(candidate);
        }

        // Access/edge sightings are the real attachment candidates; trunk sightings are demoted and only
        // considered when there is no access sighting at all (AC2 / trunk-vs-access disambiguation).
        var contenders = accessCandidates.Count > 0 ? accessCandidates : trunkCandidates;

        if (contenders.Count == 1)
        {
            mappings.Add(new NicPortMapping(serverId, nicName, nicMac, contenders[0]));
            return;
        }

        var resolved = ResolveAmbiguous(contenders, index);
        ambiguous.Add(new AmbiguousNicMapping(serverId, nicName, nicMac, resolved));
    }

    private static PortCandidate BuildCandidate(PortRef port, PortClass portClass, SnapshotIndex index)
    {
        var reasons = new List<ReasonCode>();
        double score;

        if (portClass.IsTrunk)
        {
            // A transiting MAC on a trunk/uplink — flat Low-band confidence regardless of LLDP/VLAN.
            score = CorrelationScoring.TrunkOnlyConfidence;
            reasons.Add(ReasonCode.SeenOnTrunkPort);
            if (portClass.PeerSwitchLldp)
            {
                reasons.Add(ReasonCode.LldpContradicts);
                reasons.Add(ReasonCode.ConflictingMacEvidence);
            }
        }
        else
        {
            score = CorrelationScoring.BaseBridgeHit;
            if (index.LearnedMacCount(port) <= CorrelationScoring.AccessUniqueMaxHosts)
            {
                reasons.Add(ReasonCode.MacLearnUnique);
            }

            if (index.HasLldp(port))
            {
                reasons.Add(ReasonCode.LldpConsistent);
                score += CorrelationScoring.LldpConsistentBonus;
            }
            else
            {
                reasons.Add(ReasonCode.MissingLldp);
                score += CorrelationScoring.MissingLldpBonus;
            }
        }

        var vlans = index.VlansFor(port);
        reasons.Add(vlans.Count > 0 ? ReasonCode.VlanInferred : ReasonCode.VlanContextMissing);

        return new PortCandidate(port.SwitchId, port.PortName, Score(score), vlans, reasons);
    }

    private static IReadOnlyList<PortCandidate> ResolveAmbiguous(
        IReadOnlyList<PortCandidate> contenders, SnapshotIndex index)
    {
        var isLag = IsSameLag(contenders, index);

        var adjusted = new List<PortCandidate>(contenders.Count);
        foreach (var c in contenders)
        {
            var reasons = new List<ReasonCode>(c.ReasonCodes)
            {
                ReasonCode.MultipleMacPorts,
                ReasonCode.DuplicateMac,
            };

            double score;
            if (isLag)
            {
                reasons.Add(ReasonCode.PortsInSameLag);
                score = CorrelationScoring.LagBoostedScore;
            }
            else
            {
                reasons.Add(ReasonCode.ConflictingMacEvidence);
                score = c.Confidence.Value * CorrelationScoring.AmbiguityPenaltyFactor;
            }

            adjusted.Add(c with { Confidence = Score(score), ReasonCodes = reasons });
        }

        return adjusted
            .OrderByDescending(c => c.Confidence.Value)
            .ThenBy(c => c.SwitchId, StringComparer.Ordinal)
            .ThenBy(c => c.PortName, StringComparer.Ordinal)
            .Take(MaxCandidatesPerNic)
            .ToList();
    }

    // LAG heuristic (story answered-question): all candidates on one switch sharing identical VLAN config
    // look like members of a single link-aggregation group. This is a config-shape heuristic, not real
    // LACP membership (see ADR 0010).
    private static bool IsSameLag(IReadOnlyList<PortCandidate> contenders, SnapshotIndex index)
    {
        if (contenders.Count < 2)
        {
            return false;
        }

        var first = new PortRef(contenders[0].SwitchId, contenders[0].PortName);
        var signature = index.VlanSignature(first);
        for (var i = 1; i < contenders.Count; i++)
        {
            if (!string.Equals(contenders[i].SwitchId, contenders[0].SwitchId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                    index.VlanSignature(new PortRef(contenders[i].SwitchId, contenders[i].PortName)),
                    signature,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<UnmappedPort> CollectUnmappedPorts(TopologyCorrelationInput input, SnapshotIndex index)
    {
        var result = new List<UnmappedPort>();
        foreach (var sw in input.Switches ?? [])
        {
            foreach (var port in sw.Ports ?? [])
            {
                var portRef = new PortRef(sw.SwitchId, port.PortName);
                var portClass = index.Classify(portRef);

                // Trunk/uplink ports are expected to carry foreign MACs — excluded as noise (ADR 0010).
                if (portClass.IsTrunk)
                {
                    continue;
                }

                // A port that learned a MAC some NIC owns is (or will be) a mapping, not an unmapped port.
                if (index.PortOwnedByNic(portRef))
                {
                    continue;
                }

                var hasLearned = index.LearnedMacCount(portRef) > 0;
                var hasLldp = index.HasLldp(portRef);

                // Fully idle ports (no learned MAC, no LLDP) carry no correlation signal — excluded.
                if (!hasLearned && !hasLldp)
                {
                    continue;
                }

                var reasons = new List<ReasonCode>();
                if (hasLearned)
                {
                    reasons.Add(ReasonCode.NotSeenInBmc);
                }

                if (hasLldp)
                {
                    reasons.Add(ReasonCode.PortNeighbourUnknown);
                }

                result.Add(new UnmappedPort(sw.SwitchId, port.PortName, reasons));
            }
        }

        return result;
    }

    private static ConfidenceScore Score(double raw)
    {
        var clamped = Math.Clamp(raw, ConfidenceScore.Minimum, ConfidenceScore.Maximum);
        return ConfidenceScore.From(Math.Round(clamped, CorrelationScoring.ScorePrecision));
    }
}
