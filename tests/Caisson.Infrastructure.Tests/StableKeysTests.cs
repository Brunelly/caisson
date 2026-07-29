using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of the canonical <see cref="StableKeys"/> definitions (the story's answered question).
/// These run with no database so they always execute in the codegen sandbox. Finding #3 (security-review-5):
/// every key is now prefixed with the trusted, config-supplied device key, and every composite key escapes
/// its <c>|</c> delimiter, so two configured devices — or two differently-segmented device-reported
/// values — can never collide onto the same stable key.
/// </summary>
public sealed class StableKeysTests
{
    [Fact]
    public void Switch_prefers_serial_then_management_ip_behind_the_device_key_prefix()
    {
        StableKeys.ForSwitch("dev-1", "SER-1", "10.0.0.1").Should().Be("dev-1|SER-1");
        StableKeys.ForSwitch("dev-1", null, "10.0.0.1").Should().Be("dev-1|10.0.0.1");
    }

    [Fact]
    public void Switch_without_any_identifier_throws()
    {
        var act = () => StableKeys.ForSwitch("dev-1", null, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_switches_with_an_identical_serial_but_different_device_keys_produce_distinct_keys()
    {
        var keyA = StableKeys.ForSwitch("dev-a", "SAME-SERIAL", null);
        var keyB = StableKeys.ForSwitch("dev-b", "SAME-SERIAL", null);

        keyA.Should().NotBe(keyB);
    }

    [Fact]
    public void SwitchPort_is_switch_key_pipe_port_name()
        => StableKeys.ForSwitchPort("dev-1|SER-1", "ether1").Should().Be("dev-1%7CSER-1|ether1");

    [Fact]
    public void SwitchPort_escapes_a_pipe_in_the_port_name_so_segments_cannot_collide()
    {
        // serial "S1" + port "eth0|eth1" must not collide with serial "S1|eth0" + port "eth1".
        var keyA = StableKeys.ForSwitchPort(StableKeys.ForSwitch("dev-1", "S1", null), "eth0|eth1");
        var keyB = StableKeys.ForSwitchPort(StableKeys.ForSwitch("dev-1", "S1|eth0", null), "eth1");

        keyA.Should().NotBe(keyB);
    }

    [Fact]
    public void TryForSwitchPort_succeeds_when_port_name_is_present()
    {
        StableKeys.TryForSwitchPort("dev-1|SER-1", "ether1", out var key).Should().BeTrue();
        key.Should().Be("dev-1%7CSER-1|ether1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryForSwitchPort_fails_without_throwing_when_port_name_is_blank(string? portName)
    {
        StableKeys.TryForSwitchPort("dev-1|SER-1", portName, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    [Fact]
    public void Server_prefers_bmc_uuid_then_hostname_then_bmc_address_behind_the_device_key_prefix()
    {
        StableKeys.ForServer("dev-1", "uuid-1", "host-1", "10.0.1.1").Should().Be("dev-1|uuid-1");
        StableKeys.ForServer("dev-1", null, "host-1", "10.0.1.1").Should().Be("dev-1|host-1");
        StableKeys.ForServer("dev-1", null, null, "10.0.1.1").Should().Be("dev-1|10.0.1.1");
    }

    [Fact]
    public void Two_servers_with_an_identical_uuid_but_different_device_keys_produce_distinct_keys()
    {
        var keyA = StableKeys.ForServer("dev-a", "SAME-UUID", null, null);
        var keyB = StableKeys.ForServer("dev-b", "SAME-UUID", null, null);

        keyA.Should().NotBe(keyB);
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
    public void Lldp_escapes_a_pipe_in_either_segment_so_segments_cannot_collide()
    {
        var keyA = StableKeys.ForLldp("chassis-1", "port|1");
        var keyB = StableKeys.ForLldp("chassis-1|port", "1");

        keyA.Should().NotBe(keyB);
    }

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
