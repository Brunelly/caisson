using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik.Mapping;
using Caisson.Drivers.MikroTik.Parsing;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC2/AC3: the mappers produce the correct story-3 info records from both v6- and v7-style rows, and
/// tolerate bad rows by emitting diagnostics rather than throwing.
/// </summary>
public sealed class MappingTests
{
    private readonly List<DriverDiagnostic> _diagnostics = new();

    [Fact]
    public void Device_info_maps_version_model_and_serial()
    {
        var info = RouterOsMappers.MapDeviceInfo(
            new RouterOsRecord(RouterOsFixtures.V7.Resource),
            new RouterOsRecord(RouterOsFixtures.V7.Routerboard),
            "10.0.0.1");

        info.ManagementIp.Should().Be("10.0.0.1");
        info.OsVersion.Should().Be("7.10.2");
        info.Model.Should().Be("CCR2004-1G-12S+2XS");
        info.Serial.Should().Be("HET081ABCDE");
    }

    [Fact]
    public void Device_info_serial_is_null_on_chr()
    {
        var info = RouterOsMappers.MapDeviceInfo(
            new RouterOsRecord(RouterOsFixtures.Chr.Resource),
            new RouterOsRecord(RouterOsFixtures.Chr.Routerboard),
            "10.0.0.9");

        info.Serial.Should().BeNull();
        info.Model.Should().Be("CHR");
        info.OsVersion.Should().Be("7.14.2");
    }

    [Fact]
    public void Ports_map_from_v7_with_pvid_and_inverted_tagged_vlans()
    {
        var ports = RouterOsMappers.MapPorts(
            RouterOsFixtures.V7.Interfaces, RouterOsFixtures.V7.EthernetInterfaces,
            RouterOsFixtures.V7.BridgePorts, RouterOsFixtures.V7.BridgeVlans, _diagnostics);

        var ether1 = ports.Single(p => p.PortName == "ether1");
        ether1.IsUp.Should().BeTrue();
        ether1.Pvid.Should().Be(10);
        ether1.TaggedVlans.Should().Equal(10, 20, 30, 31, 32);

        var ether2 = ports.Single(p => p.PortName == "ether2");
        ether2.IsUp.Should().BeFalse();
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Ports_map_from_v6_with_yes_no_booleans_and_trimmed_names()
    {
        var ports = RouterOsMappers.MapPorts(
            RouterOsFixtures.V6.Interfaces, RouterOsFixtures.V6.EthernetInterfaces,
            RouterOsFixtures.V6.BridgePorts, RouterOsFixtures.V6.BridgeVlans, _diagnostics);

        var ether1 = ports.Single(p => p.PortName == "ether1");
        ether1.IsUp.Should().BeTrue();
        ether1.TaggedVlans.Should().Equal(10, 20);
    }

    [Fact]
    public void Ports_are_scoped_to_physical_ethernet_interfaces_when_that_section_is_present()
    {
        var interfaces = new[]
        {
            RouterOsFixtures.Row(("name", "ether1"), ("running", "true"), ("disabled", "false")),
            RouterOsFixtures.Row(("name", "bridge1"), ("running", "true"), ("disabled", "false")),
            RouterOsFixtures.Row(("name", "vlan10"), ("running", "true"), ("disabled", "false")),
        };
        var ethernet = new[] { RouterOsFixtures.Row(("name", "ether1")) };

        var ports = RouterOsMappers.MapPorts(
            interfaces, ethernet,
            Array.Empty<IReadOnlyDictionary<string, string>>(),
            Array.Empty<IReadOnlyDictionary<string, string>>(), _diagnostics);

        // Logical interfaces (bridge, VLAN) are excluded so they cannot pollute topology; no diagnostic.
        ports.Select(p => p.PortName).Should().Equal("ether1");
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Ports_include_every_interface_when_the_ethernet_section_is_unavailable()
    {
        var interfaces = new[]
        {
            RouterOsFixtures.Row(("name", "ether1"), ("running", "true")),
            RouterOsFixtures.Row(("name", "bridge1"), ("running", "true")),
        };

        // An empty ethernet section (unsupported/errored) degrades to including all interfaces (AC3).
        var ports = RouterOsMappers.MapPorts(
            interfaces, Array.Empty<IReadOnlyDictionary<string, string>>(),
            Array.Empty<IReadOnlyDictionary<string, string>>(),
            Array.Empty<IReadOnlyDictionary<string, string>>(), _diagnostics);

        ports.Select(p => p.PortName).Should().BeEquivalentTo("ether1", "bridge1");
    }

    [Fact]
    public void Interface_row_without_a_name_yields_a_diagnostic_not_an_exception()
    {
        var rows = new[] { RouterOsFixtures.Row(("running", "true")) };

        var ports = RouterOsMappers.MapPorts(rows, Array.Empty<IReadOnlyDictionary<string, string>>(),
            Array.Empty<IReadOnlyDictionary<string, string>>(),
            Array.Empty<IReadOnlyDictionary<string, string>>(), _diagnostics);

        ports.Should().BeEmpty();
        _diagnostics.Should().ContainSingle().Which.ReasonCode.Should().Be(ReasonCode.ParseError);
    }

    [Fact]
    public void Lldp_maps_from_v7_with_remote_port_id()
    {
        var neighbours = RouterOsMappers.MapLldpNeighbours(RouterOsFixtures.V7.Neighbours, _diagnostics);

        var neighbour = neighbours.Should().ContainSingle().Subject;
        neighbour.PortName.Should().Be("ether1");
        neighbour.ChassisId.Should().Be("E4:8D:8C:11:22:33");
        neighbour.PortId.Should().Be("sfp-sfpplus1");
        neighbour.SystemName.Should().Be("core-sw");
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Lldp_maps_from_v6_using_chassis_id_field_with_empty_port_id()
    {
        var neighbours = RouterOsMappers.MapLldpNeighbours(RouterOsFixtures.V6.Neighbours, _diagnostics);

        var neighbour = neighbours.Should().ContainSingle().Subject;
        neighbour.ChassisId.Should().Be("E4:8D:8C:AA:BB:CC");
        neighbour.PortId.Should().BeEmpty();
    }

    [Fact]
    public void Empty_lldp_is_an_empty_list_with_no_diagnostic()
    {
        var neighbours = RouterOsMappers.MapLldpNeighbours(
            Array.Empty<IReadOnlyDictionary<string, string>>(), _diagnostics);

        neighbours.Should().BeEmpty();
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bridge_hosts_normalize_macs_across_v6_and_v7_field_names_and_formats()
    {
        var v7 = RouterOsMappers.MapBridgeHosts(RouterOsFixtures.V7.BridgeHosts, _diagnostics);
        var v6 = RouterOsMappers.MapBridgeHosts(RouterOsFixtures.V6.BridgeHosts, _diagnostics);

        v7.Should().ContainSingle().Which.Mac.Should().Be(MacAddressValue.Parse("AA:BB:CC:DD:EE:FF"));
        v7[0].PortName.Should().Be("ether1");
        // v6 uses the dotted lowercase form and the "on-interface" key; both normalize identically.
        v6.Should().ContainSingle().Which.Mac.Should().Be(MacAddressValue.Parse("aa:bb:cc:dd:ee:ff"));
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bridge_host_with_a_bad_mac_becomes_a_diagnostic_not_an_exception()
    {
        var rows = new[] { RouterOsFixtures.Row(("mac-address", "not-a-mac"), ("on-interface", "ether3")) };

        var entries = RouterOsMappers.MapBridgeHosts(rows, _diagnostics);

        entries.Should().BeEmpty();
        _diagnostics.Should().ContainSingle().Which.ReasonCode.Should().Be(ReasonCode.ParseError);
    }

    [Fact]
    public void Vlans_union_dedupes_by_id_and_prefers_named_interfaces()
    {
        var vlans = RouterOsMappers.MapVlans(RouterOsFixtures.V7.BridgeVlans, RouterOsFixtures.V7.VlanInterfaces, _diagnostics);

        vlans.Select(v => v.VlanId).Should().Equal(10, 20, 30, 31, 32);
        vlans.Single(v => v.VlanId == 10).Name.Should().Be("vlan10");
        vlans.Single(v => v.VlanId == 20).Name.Should().BeNull();
    }
}
