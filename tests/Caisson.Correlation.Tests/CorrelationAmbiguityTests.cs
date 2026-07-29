using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>AC3 + the LAG answered-question: multiple candidate ports are surfaced, ranked, and reasoned.</summary>
public sealed class CorrelationAmbiguityTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Duplicate_mac_across_two_switches_yields_ambiguous_mapping_with_all_candidates()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10)
                .Lldp("ether1", systemName: "server-a")
                .Bridge("ether1", "00:11:22:33:44:55"))
            .Switch("sw2", s => s
                .Port("ether5", pvid: 20)
                .Lldp("ether5", systemName: "server-a")
                .Bridge("ether5", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().BeEmpty();
        result.AmbiguousMappings.Should().HaveCount(1);

        var ambiguous = result.AmbiguousMappings[0];
        ambiguous.ServerId.Should().Be("srv-a");
        ambiguous.Candidates.Should().HaveCount(2);
        ambiguous.Candidates.Should().OnlyContain(c =>
            c.ReasonCodes.Contains(ReasonCode.MultipleMacPorts)
            && c.ReasonCodes.Contains(ReasonCode.DuplicateMac)
            && c.ReasonCodes.Contains(ReasonCode.ConflictingMacEvidence));
    }

    [Fact]
    public void Ambiguous_candidates_are_capped_to_the_top_k_by_score()
    {
        // Finding #11: one MAC learned on N ports used to produce N unranked candidate rows persisted per
        // NIC. Build far more than the cap (20) and assert the engine itself bounds its output.
        const string mac = "00:11:22:33:44:55";
        var builder = new SnapshotBuilder();
        for (var i = 0; i < 20; i++)
        {
            var switchId = $"sw{i}";
            var port = $"ether{i}";
            builder.Switch(switchId, s => s.Port(port).Bridge(port, mac));
        }

        builder.Server("srv-a", sv => sv.Nic("eth0", mac));

        var result = Engine.Correlate(builder.Build());

        result.AmbiguousMappings.Should().HaveCount(1);
        result.AmbiguousMappings[0].Candidates.Should().HaveCountLessThanOrEqualTo(16);
    }

    [Fact]
    public void Ambiguous_candidates_are_ordered_by_confidence_then_switch_then_port()
    {
        // Two candidates with identical evidence -> equal scores -> deterministic (SwitchId, PortName) tie-break.
        var input = new SnapshotBuilder()
            .Switch("sw-b", s => s
                .Port("ether9", pvid: 10)
                .Lldp("ether9", systemName: "server-a")
                .Bridge("ether9", "00:11:22:33:44:55"))
            .Switch("sw-a", s => s
                .Port("ether9", pvid: 10)
                .Lldp("ether9", systemName: "server-a")
                .Bridge("ether9", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var candidates = result.AmbiguousMappings.Should().ContainSingle().Subject.Candidates;
        candidates.Should().BeInDescendingOrder(c => c.Confidence.Value);
        // Equal scores -> ordinal switch id wins the tie-break.
        candidates[0].SwitchId.Should().Be("sw-a");
        candidates[1].SwitchId.Should().Be("sw-b");
    }

    [Fact]
    public void Lag_members_on_one_switch_with_identical_vlans_get_equal_boosted_scores_and_lag_reason()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10)
                .Port("ether2", pvid: 10)
                .Lldp("ether1", systemName: "server-a")
                .Lldp("ether2", systemName: "server-a")
                .Bridge("ether1", "00:11:22:33:44:55")
                .Bridge("ether2", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var candidates = result.AmbiguousMappings.Should().ContainSingle().Subject.Candidates;
        candidates.Should().HaveCount(2);
        candidates.Should().OnlyContain(c => c.ReasonCodes.Contains(ReasonCode.PortsInSameLag));
        candidates.Select(c => c.Confidence.Value).Distinct().Should().ContainSingle(
            "LAG members receive equal boosted confidence");
        candidates.Should().OnlyContain(c => ConfidenceBands.Of(c.Confidence) == ConfidenceBands.Band.Medium);
    }

    [Fact]
    public void Non_lag_ambiguity_scores_below_a_confident_single_mapping()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 10).Bridge("ether1", "00:11:22:33:44:55"))
            .Switch("sw2", s => s.Port("ether2", pvid: 20).Bridge("ether2", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var candidates = result.AmbiguousMappings.Should().ContainSingle().Subject.Candidates;
        candidates.Should().OnlyContain(c => c.Confidence.Value < ConfidenceBands.HighThreshold);
    }
}
