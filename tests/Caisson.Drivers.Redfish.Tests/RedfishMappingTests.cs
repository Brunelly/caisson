using System.Text.Json;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Mapping;
using Caisson.Drivers.Redfish.Model;
using Caisson.Drivers.Redfish.Serialization;
using Caisson.Drivers.Redfish.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC1/AC3/NFR5: the Redfish mappers produce the correct story-3 Bmc records from source-generated DTOs,
/// resolve identity in the UUID → SerialNumber → composite order, normalize MACs through
/// <c>MacAddressValue</c>, and tolerate missing/MAC-less data by emitting diagnostics rather than throwing.
/// </summary>
public sealed class RedfishMappingTests
{
    private readonly List<DriverDiagnostic> _diagnostics = new();

    [Fact]
    public void System_inventory_maps_uuid_serial_model_and_hostname()
    {
        var system = Deserialize(RedfishFixtures.SystemFull, RedfishJsonContext.Default.ComputerSystem);

        var inventory = RedfishMappers.MapSystemInventory(system, "10.4.7.5", _diagnostics);

        inventory.BmcType.Should().Be(BmcType.Redfish);
        inventory.BmcAddress.Should().Be("10.4.7.5");
        inventory.BmcUuid.Should().Be("38373035-3831-4247-3830-353531384752");
        inventory.Serial.Should().Be("CZ3629abcd");
        inventory.Model.Should().Be("ProLiant DL380 Gen10");
        inventory.Hostname.Should().Be("esx-node-07");
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Missing_uuid_and_serial_emit_a_degraded_identity_warning()
    {
        var system = Deserialize(RedfishFixtures.SystemNoIdentity, RedfishJsonContext.Default.ComputerSystem);

        var inventory = RedfishMappers.MapSystemInventory(system, "10.4.7.5", _diagnostics);

        inventory.BmcUuid.Should().BeNull();
        inventory.Serial.Should().BeNull();
        _diagnostics.Should().ContainSingle()
            .Which.Severity.Should().Be(DriverDiagnosticSeverity.Warning);
        _diagnostics[0].EntityRef.Should().Be("1@10.4.7.5");
        _diagnostics[0].Message.Should().Contain("degraded");
    }

    [Fact]
    public void Only_serial_present_still_resolves_without_a_warning()
    {
        var system = new ComputerSystem(
            Id: "1", Uuid: null, SerialNumber: "SN-123", Model: "DL360", Manufacturer: "HPE",
            HostName: null, BiosVersion: null, EthernetInterfaces: null, Bios: null, Links: null);

        var inventory = RedfishMappers.MapSystemInventory(system, "host", _diagnostics);

        inventory.Serial.Should().Be("SN-123");
        _diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("00-1a-2b-3c-4d-5e", "001a2b3c4d5e")]
    [InlineData("001A.2B3C.4D5E", "001a2b3c4d5e")]
    [InlineData("00:1A:2B:3C:4D:5E", "001a2b3c4d5e")]
    [InlineData("001a2b3c4d5e", "001a2b3c4d5e")]
    public void Mac_is_normalized_through_the_domain_value_object(string input, string expected)
    {
        var nic = new EthernetInterface(
            OdataId: "/redfish/v1/Systems/1/EthernetInterfaces/1", Id: "1", Name: "eth0",
            MacAddress: input, PermanentMacAddress: null, LinkStatus: "LinkUp");

        var result = RedfishMappers.MapNetworkInterfaces(new EthernetInterface?[] { nic }, _diagnostics);

        result.Should().ContainSingle();
        result[0].Mac!.Value.Value.Should().Be(expected);
        result[0].LinkState.Should().Be(LinkState.Up);
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_nic_without_a_mac_is_included_with_null_mac_and_a_per_nic_diagnostic()
    {
        var nic = Deserialize(RedfishFixtures.NicNoMac, RedfishJsonContext.Default.EthernetInterface);

        var result = RedfishMappers.MapNetworkInterfaces(new[] { nic }, _diagnostics);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("eth1");
        result[0].Mac.Should().BeNull();
        _diagnostics.Should().ContainSingle(d => d.EntityRef == "2" && d.ReasonCode == ReasonCode.ParseError);
    }

    [Fact]
    public void A_nic_with_an_unparseable_mac_is_included_with_null_mac_and_an_error_diagnostic()
    {
        var nic = new EthernetInterface(
            OdataId: null, Id: "nic7", Name: "eth7", MacAddress: "not-a-mac",
            PermanentMacAddress: null, LinkStatus: null);

        var result = RedfishMappers.MapNetworkInterfaces(new EthernetInterface?[] { nic }, _diagnostics);

        result.Should().ContainSingle();
        result[0].Mac.Should().BeNull();
        _diagnostics.Should().ContainSingle(d => d.EntityRef == "nic7" && d.Severity == DriverDiagnosticSeverity.Error);
    }

    [Fact]
    public void The_nic_id_falls_back_to_the_trailing_odata_segment_when_id_is_absent()
    {
        var nic = new EthernetInterface(
            OdataId: "/redfish/v1/Systems/1/EthernetInterfaces/NIC.Integrated.1", Id: null, Name: null,
            MacAddress: null, PermanentMacAddress: null, LinkStatus: null);

        RedfishMappers.MapNetworkInterfaces(new EthernetInterface?[] { nic }, _diagnostics);

        _diagnostics.Should().ContainSingle().Which.EntityRef.Should().Be("NIC.Integrated.1");
    }

    [Fact]
    public void An_empty_nic_list_yields_no_interfaces_and_no_diagnostics()
    {
        var result = RedfishMappers.MapNetworkInterfaces(Array.Empty<EthernetInterface?>(), _diagnostics);

        result.Should().BeEmpty();
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Permanent_mac_is_used_when_the_current_mac_is_absent()
    {
        var nic = new EthernetInterface(
            OdataId: null, Id: "1", Name: "eth0", MacAddress: null,
            PermanentMacAddress: "aa:bb:cc:dd:ee:ff", LinkStatus: "LinkUp");

        var result = RedfishMappers.MapNetworkInterfaces(new EthernetInterface?[] { nic }, _diagnostics);

        result[0].Mac!.Value.Value.Should().Be("aabbccddeeff");
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bios_info_maps_vendor_and_version()
    {
        var system = Deserialize(RedfishFixtures.SystemFull, RedfishJsonContext.Default.ComputerSystem);

        var bios = RedfishMappers.MapBiosInfo(system, _diagnostics);

        bios.Vendor.Should().Be("HPE");
        bios.Version.Should().Be("U30 v2.60");
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Missing_bios_version_degrades_to_a_diagnostic()
    {
        var system = new ComputerSystem(
            Id: "1", Uuid: "u", SerialNumber: null, Model: null, Manufacturer: "HPE",
            HostName: null, BiosVersion: null, EthernetInterfaces: null, Bios: null, Links: null);

        var bios = RedfishMappers.MapBiosInfo(system, _diagnostics);

        bios.Version.Should().BeNull();
        _diagnostics.Should().ContainSingle(d => d.EntityRef == "Bios");
    }

    [Fact]
    public void Member_links_tolerate_a_null_collection()
        => RedfishMappers.MemberLinks(null).Should().BeEmpty();

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
        => JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new InvalidOperationException("Fixture deserialized to null.");
}
