using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>AC4: unmapped NICs and unmapped ports are explicitly represented with reasons, never dropped.</summary>
public sealed class CorrelationUnmappedTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Nic_mac_absent_from_every_switch_table_is_unmapped_with_not_seen_in_switch()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 1).Bridge("ether1", "00:00:00:00:00:01"))
            .Server("srv-a", sv => sv.Nic("eth0", "de:ad:be:ef:00:99"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().BeEmpty();
        var unmapped = result.UnmappedNics.Should().ContainSingle().Subject;
        unmapped.ServerId.Should().Be("srv-a");
        unmapped.NicName.Should().Be("eth0");
        unmapped.ReasonCodes.Should().ContainSingle().Which.Should().Be(ReasonCode.NotSeenInSwitch);
    }

    [Fact]
    public void Nic_without_a_parseable_mac_is_unmapped_with_parse_error()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 1))
            .Server("srv-a", sv => sv.Nic("eth0", mac: null))
            .Build();

        var result = Engine.Correlate(input);

        var unmapped = result.UnmappedNics.Should().ContainSingle().Subject;
        unmapped.ReasonCodes.Should().ContainSingle().Which.Should().Be(ReasonCode.ParseError);
    }

    [Fact]
    public void Access_port_with_learned_unowned_mac_and_unknown_lldp_neighbour_is_unmapped_with_both_reasons()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether7", pvid: 1)
                .Lldp("ether7", chassisId: "ff:ff:ff:00:00:aa", systemName: "mystery-box")
                .Bridge("ether7", "ca:fe:00:00:00:01"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        // The NIC MAC was never seen -> unmapped NIC; the port learned an unowned MAC + has an LLDP peer.
        result.UnmappedNics.Should().ContainSingle();
        var port = result.UnmappedPorts.Should().ContainSingle().Subject;
        port.SwitchId.Should().Be("sw1");
        port.PortName.Should().Be("ether7");
        port.ReasonCodes.Should().Contain(new[] { ReasonCode.NotSeenInBmc, ReasonCode.PortNeighbourUnknown });
    }

    [Fact]
    public void Access_port_with_only_an_unknown_lldp_neighbour_is_unmapped_with_neighbour_reason_only()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether3", pvid: 1)
                .Lldp("ether3", systemName: "mystery-box"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        var port = result.UnmappedPorts.Should().ContainSingle().Subject;
        port.ReasonCodes.Should().ContainSingle().Which.Should().Be(ReasonCode.PortNeighbourUnknown);
    }

    [Fact]
    public void Idle_ports_and_mapped_ports_are_not_reported_as_unmapped()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s
                .Port("ether1", pvid: 10) // maps to srv-a
                .Port("ether2", pvid: 10) // idle: no MAC, no LLDP
                .Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var result = Engine.Correlate(input);

        result.Mappings.Should().ContainSingle();
        result.UnmappedPorts.Should().BeEmpty();
    }
}
