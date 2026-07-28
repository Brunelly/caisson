using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>The bridge table alone still maps a NIC when no LLDP evidence is present (AC2 fallback).</summary>
public sealed class MissingLldpFallbackTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Unique_bridge_hit_without_lldp_still_maps_and_flags_missing_lldp()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10)
                .Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var mapping = result.Mappings.Should().ContainSingle().Subject;
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.MissingLldp);
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.MacLearnUnique);
        mapping.Port.ReasonCodes.Should().NotContain(ReasonCode.LldpConsistent);
    }

    [Fact]
    public void Lldp_consistent_scores_higher_than_missing_lldp_for_the_same_port()
    {
        var withLldp = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 10).Lldp("ether1", systemName: "server-a").Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var withoutLldp = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 10).Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var consistent = Engine.Correlate(withLldp).Mappings[0].Port.Confidence.Value;
        var missing = Engine.Correlate(withoutLldp).Mappings[0].Port.Confidence.Value;

        consistent.Should().BeGreaterThan(missing);
        ConfidenceBands.Of(missing).Should().Be(ConfidenceBands.Band.High);
    }
}
