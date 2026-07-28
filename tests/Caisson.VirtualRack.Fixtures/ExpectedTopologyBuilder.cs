using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// Builds the <see cref="TopologyCorrelationResult"/> the real <c>ITopologyCorrelationEngine</c> must
/// reproduce from <see cref="VirtualRackDefinition"/>, hand-derived from the engine's documented scoring
/// rules (<c>CorrelationScoring</c>, ADR 0010) rather than by calling the engine — so a regression in the
/// engine shows up as a diff against an independent expectation, not a tautology.
/// </summary>
public static class ExpectedTopologyBuilder
{
    /// <summary>Builds the expected correlation result for <see cref="VirtualRackDefinition"/>.</summary>
    public static TopologyCorrelationResult Build()
    {
        var mappings = new List<NicPortMapping>
        {
            new(
                VirtualRackDefinition.ServerId,
                VirtualRackDefinition.CleanNicName,
                VirtualRackDefinition.CleanMac,
                new PortCandidate(
                    VirtualRackDefinition.SwitchId,
                    VirtualRackDefinition.CleanPort,
                    ConfidenceScore.From(0.95),
                    new[] { VirtualRackDefinition.CleanVlan },
                    new[] { ReasonCode.MacLearnUnique, ReasonCode.LldpConsistent, ReasonCode.VlanInferred })),
        };

        // Same MAC learned on two access ports with different VLANs (not a LAG — different VLAN
        // signature), so both candidates carry ConflictingMacEvidence rather than PortsInSameLag,
        // ordered by descending confidence then (SwitchId, PortName).
        var ambiguousMappings = new List<AmbiguousNicMapping>
        {
            new(
                VirtualRackDefinition.ServerId,
                VirtualRackDefinition.AmbiguousNicName,
                VirtualRackDefinition.AmbiguousMac,
                new[]
                {
                    AmbiguousCandidate(VirtualRackDefinition.AmbiguousPortA, VirtualRackDefinition.AmbiguousVlanA),
                    AmbiguousCandidate(VirtualRackDefinition.AmbiguousPortB, VirtualRackDefinition.AmbiguousVlanB),
                }),
        };

        var unmappedNics = new List<UnmappedNic>
        {
            new(VirtualRackDefinition.ServerId, VirtualRackDefinition.UnmappedNicName, new[] { ReasonCode.NotSeenInSwitch }),
        };

        var unmappedPorts = new List<UnmappedPort>
        {
            new(VirtualRackDefinition.SwitchId, VirtualRackDefinition.UnmappedPort, new[] { ReasonCode.NotSeenInBmc }),
        };

        return new TopologyCorrelationResult(mappings, ambiguousMappings, unmappedNics, unmappedPorts);
    }

    private static PortCandidate AmbiguousCandidate(string portName, int vlan)
        => new(
            VirtualRackDefinition.SwitchId,
            portName,
            ConfidenceScore.From(0.51),
            new[] { vlan },
            new[]
            {
                ReasonCode.MacLearnUnique,
                ReasonCode.MissingLldp,
                ReasonCode.VlanInferred,
                ReasonCode.MultipleMacPorts,
                ReasonCode.DuplicateMac,
                ReasonCode.ConflictingMacEvidence,
            });
}
