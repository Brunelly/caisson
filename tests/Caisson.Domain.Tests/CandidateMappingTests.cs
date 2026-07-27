using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

public sealed class CandidateMappingTests
{
    private static readonly Guid Rack = Guid.NewGuid();
    private static readonly Guid Snapshot = Guid.NewGuid();
    private static readonly Guid Nic = Guid.NewGuid();

    [Fact]
    public void Multiple_candidates_for_the_same_nic_can_be_ordered_by_confidence_descending()
    {
        var low = Mapping(0.20, ReasonCode.MissingLldp, Guid.NewGuid());
        var high = Mapping(0.90, ReasonCode.Unknown, Guid.NewGuid());
        var mid = Mapping(0.55, ReasonCode.ConflictingMacEvidence, Guid.NewGuid());

        var ordered = new[] { low, high, mid }
            .OrderByDescending(m => m.Confidence.Value)
            .ToList();

        ordered.Should().ContainInOrder(high, mid, low);
        ordered.Should().OnlyContain(m => m.NicId == Nic);
    }

    [Fact]
    public void An_unmapped_candidate_is_representable_with_a_null_switch_port()
    {
        var unmapped = Mapping(0.0, ReasonCode.NotSeenInSwitch, switchPortId: null);

        unmapped.SwitchPortId.Should().BeNull();
        unmapped.ReasonCode.Should().Be(ReasonCode.NotSeenInSwitch);
    }

    [Fact]
    public void Evidence_exceeding_the_bound_is_rejected()
    {
        var tooBig = new string('x', TopologyCandidateMapping.MaxEvidenceJsonLength + 1);

        var act = () => Mapping(0.5, ReasonCode.Unknown, Guid.NewGuid(), tooBig);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Bounded_evidence_is_accepted_and_stored()
    {
        const string evidence = "{\"bridgePorts\":[\"Gi1/0/1\"]}";

        var mapping = Mapping(0.8, ReasonCode.Unknown, Guid.NewGuid(), evidence);

        mapping.EvidenceJson.Should().Be(evidence);
    }

    private static TopologyCandidateMapping Mapping(
        double confidence, ReasonCode reason, Guid? switchPortId, string? evidence = null)
        => new(
            Guid.NewGuid(),
            Rack,
            Snapshot,
            Nic,
            ConfidenceScore.From(confidence),
            reason,
            switchPortId,
            evidence);
}
