using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Mapping;
using Caisson.Drivers.Redfish.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC2/AC3: the tolerant IPMI text parser turns <c>ipmitool</c> output into the same story-3 Bmc records,
/// degrading malformed lines to diagnostics and never throwing, so IPMI-sourced data is usable and
/// indistinguishable downstream except for its provenance.
/// </summary>
public sealed class IpmiParsingTests
{
    private readonly List<DriverDiagnostic> _diagnostics = new();

    [Fact]
    public void Fru_and_mc_info_map_serial_and_model_into_system_inventory()
    {
        var mcInfo = IpmiOutputParser.Parse(RedfishFixtures.IpmiMcInfo, "mc info", _diagnostics);
        var fru = IpmiOutputParser.Parse(RedfishFixtures.IpmiFruPrint, "fru print", _diagnostics);

        var inventory = IpmiOutputParser.MapSystemInventory(mcInfo, fru, "10.4.7.5", _diagnostics);

        inventory.BmcType.Should().Be(BmcType.Redfish);
        inventory.Serial.Should().Be("CZ3629abcd");
        inventory.Model.Should().Be("ProLiant DL380 Gen10");
    }

    [Fact]
    public void Missing_fru_serial_emits_a_degraded_identity_warning()
    {
        var mcInfo = IpmiOutputParser.Parse(RedfishFixtures.IpmiMcInfo, "mc info", _diagnostics);
        var fru = IpmiOutputParser.Parse("Board Product : DL380", "fru print", _diagnostics);

        var inventory = IpmiOutputParser.MapSystemInventory(mcInfo, fru, "10.4.7.5", _diagnostics);

        inventory.Serial.Should().BeNull();
        _diagnostics.Should().Contain(d => d.Message.Contains("degraded") && d.EntityRef == "unknown@10.4.7.5");
    }

    [Fact]
    public void Lan_print_maps_and_normalizes_the_bmc_mac()
    {
        var lan = IpmiOutputParser.Parse(RedfishFixtures.IpmiLanPrint, "lan print", _diagnostics);

        var nics = IpmiOutputParser.MapNetworkInterfaces(lan, _diagnostics);

        nics.Should().ContainSingle();
        nics[0].Name.Should().Be("ipmi-lan");
        nics[0].Mac!.Value.Value.Should().Be("001a2b3c4d99");
    }

    [Fact]
    public void Lan_print_without_a_mac_yields_a_null_mac_nic_and_a_diagnostic()
    {
        var lan = IpmiOutputParser.Parse("IP Address : 10.0.0.5", "lan print", _diagnostics);

        var nics = IpmiOutputParser.MapNetworkInterfaces(lan, _diagnostics);

        nics.Should().ContainSingle();
        nics[0].Mac.Should().BeNull();
        _diagnostics.Should().Contain(d => d.EntityRef == "ipmi-lan" && d.ReasonCode == ReasonCode.ParseError);
    }

    [Fact]
    public void A_malformed_line_degrades_to_a_diagnostic_without_throwing()
    {
        var record = IpmiOutputParser.Parse("this line has no separator\nMAC Address : 00:11:22:33:44:55", "lan print", _diagnostics);

        record.GetString("MAC Address").Should().Be("00:11:22:33:44:55");
        _diagnostics.Should().Contain(d => d.Severity == DriverDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Empty_output_parses_to_an_empty_record_without_throwing()
    {
        var record = IpmiOutputParser.Parse(string.Empty, "mc info", _diagnostics);

        record.Raw.Should().BeEmpty();
        _diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bios_info_recovers_the_vendor_from_fru_and_warns_that_version_is_unavailable()
    {
        var mcInfo = IpmiOutputParser.Parse(RedfishFixtures.IpmiMcInfo, "mc info", _diagnostics);
        var fru = IpmiOutputParser.Parse(RedfishFixtures.IpmiFruPrint, "fru print", _diagnostics);

        var bios = IpmiOutputParser.MapBiosInfo(mcInfo, fru, _diagnostics);

        bios.Vendor.Should().Be("HPE");
        bios.Version.Should().BeNull();
        _diagnostics.Should().Contain(d => d.EntityRef == "Bios");
    }
}
