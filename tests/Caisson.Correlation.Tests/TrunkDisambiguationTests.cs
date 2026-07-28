using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>
/// A MAC seen on a trunk/uplink is not a direct attachment. These tests cover the access-vs-trunk
/// disambiguation using both the tagged-VLAN-breadth and the LLDP peer-switch signals.
/// </summary>
public sealed class TrunkDisambiguationTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Mac_on_access_and_multi_vlan_trunk_maps_to_the_access_port_and_demotes_the_trunk()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10) // access/edge
                .Port("ether24", tagged: new[] { 10, 20, 30 }) // multi-VLAN trunk
                .Lldp("ether1", systemName: "server-a")
                .Bridge("ether1", "00:11:22:33:44:55")
                .Bridge("ether24", "00:11:22:33:44:55")) // same MAC transiting the trunk
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.AmbiguousMappings.Should().BeEmpty();
        var mapping = result.Mappings.Should().ContainSingle().Subject;
        mapping.Port.PortName.Should().Be("ether1");
        ConfidenceBands.Of(mapping.Port.Confidence).Should().Be(ConfidenceBands.Band.High);
        mapping.Port.ReasonCodes.Should().NotContain(ReasonCode.SeenOnTrunkPort);
        // The trunk port carries a foreign-looking MAC but is intentionally excluded from unmapped ports.
        result.UnmappedPorts.Should().NotContain(p => p.PortName == "ether24");
    }

    [Fact]
    public void Uplink_detected_via_lldp_peer_switch_is_treated_as_a_trunk()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Device(managementIp: "10.0.0.1")
                .Port("ether1", pvid: 10)
                .Port("ether48")
                .Lldp("ether1", systemName: "server-a")
                .Lldp("ether48", systemName: "sw2") // neighbour is the other switch -> uplink
                .Bridge("ether1", "00:11:22:33:44:55")
                .Bridge("ether48", "00:11:22:33:44:55"))
            .Switch("sw2", s => s
                .Device(managementIp: "10.0.0.2")
                .Port("ether48"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var mapping = result.Mappings.Should().ContainSingle().Subject;
        mapping.Port.PortName.Should().Be("ether1");
        result.UnmappedPorts.Should().NotContain(p => p.PortName == "ether48");
    }

    [Fact]
    public void Mac_seen_only_on_a_trunk_yields_a_low_band_mapping_flagged_seen_on_trunk_port()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether48", tagged: new[] { 10, 20 }) // trunk only
                .Bridge("ether48", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.AmbiguousMappings.Should().BeEmpty();
        var mapping = result.Mappings.Should().ContainSingle().Subject;
        mapping.Port.PortName.Should().Be("ether48");
        ConfidenceBands.Of(mapping.Port.Confidence).Should().Be(ConfidenceBands.Band.Low);
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.SeenOnTrunkPort);
    }

    [Fact]
    public void Port_with_many_learned_macs_is_treated_as_a_trunk()
    {
        var switchBuilder = new SnapshotBuilder()
            .Switch("sw1", s =>
            {
                s.Port("ether1", pvid: 10);
                s.Bridge("ether1", "00:11:22:33:44:55"); // the server MAC
                // Five extra foreign MACs push the port above the trunk MAC-count threshold (4).
                for (var i = 1; i <= 5; i++)
                {
                    s.Bridge("ether1", $"00:00:00:00:00:0{i}");
                }
            })
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(switchBuilder);

        var mapping = result.Mappings.Should().ContainSingle().Subject;
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.SeenOnTrunkPort);
        ConfidenceBands.Of(mapping.Port.Confidence).Should().Be(ConfidenceBands.Band.Low);
    }
}
