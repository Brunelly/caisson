using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of the canonical <see cref="StableKeys"/> definitions (the story's answered question).
/// These run with no database so they always execute in the codegen sandbox.
/// </summary>
public sealed class StableKeysTests
{
    [Fact]
    public void Switch_prefers_serial_then_management_ip()
    {
        StableKeys.ForSwitch("SER-1", "10.0.0.1").Should().Be("SER-1");
        StableKeys.ForSwitch(null, "10.0.0.1").Should().Be("10.0.0.1");
    }

    [Fact]
    public void Switch_without_any_identifier_throws()
    {
        var act = () => StableKeys.ForSwitch(null, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SwitchPort_is_switch_key_pipe_port_name()
        => StableKeys.ForSwitchPort("SER-1", "ether1").Should().Be("SER-1|ether1");

    [Fact]
    public void TryForSwitchPort_succeeds_when_port_name_is_present()
    {
        StableKeys.TryForSwitchPort("SER-1", "ether1", out var key).Should().BeTrue();
        key.Should().Be("SER-1|ether1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryForSwitchPort_fails_without_throwing_when_port_name_is_blank(string? portName)
    {
        StableKeys.TryForSwitchPort("SER-1", portName, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    [Fact]
    public void Server_prefers_bmc_uuid_then_hostname_then_bmc_address()
    {
        StableKeys.ForServer("uuid-1", "host-1", "10.0.1.1").Should().Be("uuid-1");
        StableKeys.ForServer(null, "host-1", "10.0.1.1").Should().Be("host-1");
        StableKeys.ForServer(null, null, "10.0.1.1").Should().Be("10.0.1.1");
    }

    [Fact]
    public void Nic_is_the_normalized_mac()
        => StableKeys.ForNic(MacAddressValue.Parse("AA:BB:CC:DD:EE:FF")).Should().Be("aabbccddeeff");

    [Fact]
    public void Vlan_is_the_vlan_id()
        => StableKeys.ForVlan(100).Should().Be("100");

    [Fact]
    public void Mac_is_normalized_mac_pipe_source()
        => StableKeys.ForMac(MacAddressValue.Parse("aabbccddeeff"), MacSource.Switch)
            .Should().Be("aabbccddeeff|Switch");

    [Fact]
    public void Lldp_is_chassis_pipe_port()
        => StableKeys.ForLldp("chassis-1", "port-1").Should().Be("chassis-1|port-1");

    [Fact]
    public void TryForLldp_succeeds_when_both_identifiers_are_present()
    {
        var neighbour = new LldpNeighbour(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "chassis-1", "port-1");

        StableKeys.TryForLldp(neighbour, out var key).Should().BeTrue();
        key.Should().Be("chassis-1|port-1");
    }

    [Theory]
    [InlineData("", "port-1")]
    [InlineData("chassis-1", "")]
    [InlineData("", "")]
    public void TryForLldp_fails_without_throwing_when_an_identifier_is_empty(string chassisId, string portId)
    {
        var neighbour = new LldpNeighbour(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), chassisId, portId);

        StableKeys.TryForLldp(neighbour, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }
}
