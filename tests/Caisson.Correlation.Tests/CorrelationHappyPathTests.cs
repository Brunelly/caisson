using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>AC2: a unique NIC→port→VLAN mapping is inferred with High confidence and explaining reasons.</summary>
public sealed class CorrelationHappyPathTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Clean_one_to_one_on_access_port_yields_single_high_confidence_mapping()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10)
                .Lldp("ether1", chassisId: "aa:bb:cc:00:00:01", systemName: "server-a")
                .Bridge("ether1", "00:11:22:33:44:55")
                .Vlan(10, "app"))
            .Server("srv-a", sv => sv
                .Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().HaveCount(1);
        result.AmbiguousMappings.Should().BeEmpty();
        result.UnmappedNics.Should().BeEmpty();
        result.UnmappedPorts.Should().BeEmpty();

        var mapping = result.Mappings[0];
        mapping.ServerId.Should().Be("srv-a");
        mapping.NicName.Should().Be("eth0");
        mapping.Port.SwitchId.Should().Be("sw1");
        mapping.Port.PortName.Should().Be("ether1");
        mapping.Port.Vlans.Should().Equal(10);

        ConfidenceBands.Of(mapping.Port.Confidence).Should().Be(ConfidenceBands.Band.High);
        mapping.Port.ReasonCodes.Should().Contain(new[]
        {
            ReasonCode.MacLearnUnique,
            ReasonCode.LldpConsistent,
            ReasonCode.VlanInferred,
        });
    }

    [Fact]
    public void Mac_is_normalized_before_matching_regardless_of_source_format()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 1)
                .Bridge("ether1", "AA-BB-CC-DD-EE-FF"))
            .Server("srv-a", sv => sv
                .Nic("eth0", "aabb.ccdd.eeff"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().HaveCount(1);
        result.Mappings[0].Port.PortName.Should().Be("ether1");
    }

    [Fact]
    public void Vlan_is_derived_from_pvid_and_tagged_vlans_sorted_and_distinct()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 20, tagged: new[] { 30, 10, 20 })
                .Lldp("ether1", systemName: "server-a")
                .Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().HaveCount(1);
        // Distinct + ascending; Pvid 20 folded in with the tagged set.
        result.Mappings[0].Port.Vlans.Should().Equal(10, 20, 30);
        result.Mappings[0].Port.ReasonCodes.Should().Contain(ReasonCode.VlanInferred);
    }
}
