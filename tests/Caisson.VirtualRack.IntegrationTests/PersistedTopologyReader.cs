using System.Text.Json;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.VirtualRack.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Reconstructs a <see cref="TopologyCorrelationResult"/> from what the real ingestion service actually
/// persisted for one snapshot, so the happy-path test can run it through the same
/// <see cref="TopologyDiff"/> the fixtures library uses for a raw engine result — proving discovery →
/// correlation → <b>persistence</b> fidelity, not just correlation fidelity. Reason codes are read from
/// each <see cref="TopologyCandidateMapping.EvidenceJson"/> payload (the full list the engine computed),
/// not the single collapsed <see cref="TopologyCandidateMapping.ReasonCode"/> column the read API exposes
/// — this is what lets the happy-path assertion see <c>LldpConsistent</c>, not just the primary reason.
/// <para>
/// One simplification, valid only because <see cref="VirtualRackDefinition"/> has no trunk ports: unmapped
/// ports are derived here as "any port no candidate mapping references", the same anti-join the read API
/// uses. Unmapped-port reason codes are never persisted at all (<c>UnmappedPorts</c> are ordinary
/// <c>SwitchPort</c> rows, not candidate rows — see <c>TopologySnapshotMapper</c>), so callers must not
/// require them from the result this method returns.
/// </para>
/// </summary>
public static class PersistedTopologyReader
{
    public static async Task<TopologyCorrelationResult> LoadAsync(CaissonDbContext db, Guid snapshotId)
    {
        var nics = await db.Nics.AsNoTracking().Where(n => n.SnapshotId == snapshotId).ToListAsync();
        var ports = await db.SwitchPorts.AsNoTracking().Where(p => p.SnapshotId == snapshotId).ToListAsync();
        var candidates = await db.CandidateMappings.AsNoTracking().Where(c => c.SnapshotId == snapshotId).ToListAsync();

        var nicById = nics.ToDictionary(n => n.Id);
        var portById = ports.ToDictionary(p => p.Id);

        var mappings = new List<NicPortMapping>();
        var ambiguous = new List<AmbiguousNicMapping>();
        var unmappedNics = new List<UnmappedNic>();

        foreach (var group in candidates.GroupBy(c => c.NicId))
        {
            if (!nicById.TryGetValue(group.Key, out var nic))
            {
                continue;
            }

            var mapped = group.Where(c => c.SwitchPortId is not null).ToList();
            if (mapped.Count == 0)
            {
                var (reasonCodes, _) = ParseEvidence(group.First().EvidenceJson, group.First().ReasonCode);
                unmappedNics.Add(new UnmappedNic(VirtualRackDefinition.ServerId, nic.Name, reasonCodes));
                continue;
            }

            var candidatePorts = mapped
                .Select(c => ToPortCandidate(c, portById[c.SwitchPortId!.Value]))
                .OrderByDescending(c => c.Confidence.Value)
                .ThenBy(c => c.PortName, StringComparer.Ordinal)
                .ToList();

            if (candidatePorts.Count == 1)
            {
                mappings.Add(new NicPortMapping(VirtualRackDefinition.ServerId, nic.Name, nic.MacPrimary, candidatePorts[0]));
            }
            else
            {
                ambiguous.Add(new AmbiguousNicMapping(VirtualRackDefinition.ServerId, nic.Name, nic.MacPrimary, candidatePorts));
            }
        }

        var referencedPortIds = candidates
            .Where(c => c.SwitchPortId is not null)
            .Select(c => c.SwitchPortId!.Value)
            .ToHashSet();
        var unmappedPorts = ports
            .Where(p => !referencedPortIds.Contains(p.Id))
            .Select(p => new UnmappedPort(VirtualRackDefinition.SwitchId, p.PortName, Array.Empty<ReasonCode>()))
            .ToList();

        return new TopologyCorrelationResult(mappings, ambiguous, unmappedNics, unmappedPorts);
    }

    private static PortCandidate ToPortCandidate(Domain.Topology.TopologyCandidateMapping candidate, Domain.Topology.SwitchPort port)
    {
        var (reasonCodes, vlans) = ParseEvidence(candidate.EvidenceJson, candidate.ReasonCode);
        return new PortCandidate(VirtualRackDefinition.SwitchId, port.PortName, candidate.Confidence, vlans, reasonCodes);
    }

    private static (IReadOnlyList<ReasonCode> ReasonCodes, IReadOnlyList<int> Vlans) ParseEvidence(
        string? evidenceJson, ReasonCode fallback)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            return (new[] { fallback }, Array.Empty<int>());
        }

        using var document = JsonDocument.Parse(evidenceJson);
        var root = document.RootElement;

        var reasonCodes = root.TryGetProperty("reasonCodes", out var reasonCodesElement)
            ? reasonCodesElement.EnumerateArray().Select(e => Enum.Parse<ReasonCode>(e.GetString()!)).ToList()
            : new List<ReasonCode> { fallback };

        var vlans = root.TryGetProperty("vlans", out var vlansElement)
            ? vlansElement.EnumerateArray().Select(e => e.GetInt32()).ToList()
            : new List<int>();

        return (reasonCodes, vlans);
    }
}
