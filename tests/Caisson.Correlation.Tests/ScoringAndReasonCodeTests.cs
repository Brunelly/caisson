using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>
/// AC6 + NFR4: scoring bands, ordering, and the guarantee that every returned record carries at least one
/// reason code.
/// </summary>
public sealed class ScoringAndReasonCodeTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    private static TopologyCorrelationInput MixedSnapshot() => new SnapshotBuilder()
        .Switch("sw1", s => s
            .Port("ether1", pvid: 10) // clean mapping for srv-a
            .Port("ether2", pvid: 20) // duplicate-mac candidate for srv-b
            .Port("ether7", pvid: 30) // unowned learned MAC + unknown LLDP -> unmapped port
            .Lldp("ether1", systemName: "server-a")
            .Lldp("ether7", systemName: "mystery")
            .Bridge("ether1", "00:11:22:33:44:01")
            .Bridge("ether2", "00:11:22:33:44:02")
            .Bridge("ether7", "ca:fe:00:00:00:09"))
        .Switch("sw2", s => s
            .Port("ether2", pvid: 40) // duplicate-mac candidate for srv-b
            .Bridge("ether2", "00:11:22:33:44:02"))
        .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:01")) // -> mapping
        .Server("srv-b", sv => sv.Nic("eth0", "00:11:22:33:44:02")) // -> ambiguous
        .Server("srv-c", sv => sv.Nic("eth0", "de:ad:00:00:00:99")) // -> unmapped NIC
        .Build();

    [Fact]
    public void Every_returned_record_carries_at_least_one_reason_code()
    {
        var result = Engine.Correlate(MixedSnapshot());

        result.Mappings.Should().NotBeEmpty();
        result.AmbiguousMappings.Should().NotBeEmpty();
        result.UnmappedNics.Should().NotBeEmpty();
        result.UnmappedPorts.Should().NotBeEmpty();

        result.Mappings.Should().OnlyContain(m => m.Port.ReasonCodes.Count > 0);
        result.AmbiguousMappings.Should().OnlyContain(a => a.Candidates.Count > 0);
        result.AmbiguousMappings.SelectMany(a => a.Candidates)
            .Should().OnlyContain(c => c.ReasonCodes.Count > 0);
        result.UnmappedNics.Should().OnlyContain(u => u.ReasonCodes.Count > 0);
        result.UnmappedPorts.Should().OnlyContain(u => u.ReasonCodes.Count > 0);
    }

    [Fact]
    public void Confidence_bands_follow_the_documented_thresholds()
    {
        ConfidenceBands.Of(ConfidenceScore.From(0.80)).Should().Be(ConfidenceBands.Band.High);
        ConfidenceBands.Of(ConfidenceScore.From(0.799999)).Should().Be(ConfidenceBands.Band.Medium);
        ConfidenceBands.Of(ConfidenceScore.From(0.50)).Should().Be(ConfidenceBands.Band.Medium);
        ConfidenceBands.Of(ConfidenceScore.From(0.499999)).Should().Be(ConfidenceBands.Band.Low);
        ConfidenceBands.Of(ConfidenceScore.From(0.0)).Should().Be(ConfidenceBands.Band.Low);
    }

    [Fact]
    public void All_scores_are_within_the_confidence_bounds()
    {
        var result = Engine.Correlate(MixedSnapshot());

        var allScores = result.Mappings.Select(m => m.Port.Confidence.Value)
            .Concat(result.AmbiguousMappings.SelectMany(a => a.Candidates).Select(c => c.Confidence.Value));

        allScores.Should().OnlyContain(v => v >= ConfidenceScore.Minimum && v <= ConfidenceScore.Maximum);
    }
}
