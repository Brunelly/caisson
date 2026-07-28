using System.Net.Http;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Tests.Fixtures;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC2: the per-method Redfish-first / IPMI-fallback decision. Redfish-unreachable and Redfish-returns-a
/// -MAC-less-NIC-list both trigger the read-only IPMI fallback, with the data-source provenance recorded as
/// a <see cref="ReasonCode.FallbackSource"/> diagnostic; a fully successful Redfish read never invokes IPMI.
/// </summary>
public sealed class FallbackDecisionTests : IDisposable
{
    private readonly RedfishDriverHarness _harness = new();

    [Fact]
    public async Task Redfish_unreachable_falls_back_to_ipmi_for_system_inventory_with_provenance()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath, () => new HttpRequestException("unreachable"));

        var runner = new StubIpmiCommandRunner();
        runner.SetOutput(IpmiReadCommands.McInfo, RedfishFixtures.IpmiMcInfo);
        runner.SetOutput(IpmiReadCommands.FruPrint, RedfishFixtures.IpmiFruPrint);

        var result = await _harness.Build(client, runner).GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.Serial.Should().Be("CZ3629abcd", "the serial was sourced from IPMI FRU data");
        result.Diagnostics.Should().Contain(d => d.ReasonCode == ReasonCode.FallbackSource);
        runner.Invocations.Should().Contain("fru print");
    }

    [Fact]
    public async Task A_mac_less_redfish_nic_list_falls_back_to_ipmi_for_network_interfaces()
    {
        // Full Redfish navigation, but the only NIC reports no MAC — structurally insufficient (AC2/AC3).
        var client = RedfishFixtures.SuccessClient();
        client.SetJson(RedfishFixtures.EthernetCollectionPath, """
            { "Members": [ { "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/2" } ] }
            """);
        client.SetJson(RedfishFixtures.Nic2Path, RedfishFixtures.NicNoMac);

        var runner = new StubIpmiCommandRunner();
        runner.SetOutput(IpmiReadCommands.LanPrint, RedfishFixtures.IpmiLanPrint);

        var result = await _harness.Build(client, runner).GetNetworkInterfacesAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.Should().ContainSingle(n => n.Mac != null && n.Mac.Value.Value == "001a2b3c4d99");
        result.Diagnostics.Should().Contain(d => d.ReasonCode == ReasonCode.FallbackSource);
        runner.Invocations.Should().Contain("lan print");
    }

    [Fact]
    public async Task A_fully_successful_redfish_read_never_invokes_ipmi()
    {
        var client = RedfishFixtures.SuccessClient();
        var runner = new StubIpmiCommandRunner();

        var result = await _harness.Build(client, runner).GetNetworkInterfacesAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value!.Select(n => n.Mac!.Value.Value).Should().Contain("001a2b3c4d5e");
        result.Diagnostics.Should().NotContain(d => d.ReasonCode == ReasonCode.FallbackSource);
        runner.Invocations.Should().BeEmpty("Redfish was sufficient, so IPMI must not run");
    }

    [Fact]
    public async Task When_both_redfish_and_ipmi_fail_the_call_returns_a_structured_failure()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath, () => new HttpRequestException("unreachable"));

        // IPMI credentials cannot be resolved → the fallback produces no data either.
        var runner = new StubIpmiCommandRunner();
        var driver = _harness.Build(
            client, runner,
            ipmiSettings: () => throw new BmcCredentialResolutionException("no ipmi creds"));

        var result = await driver.GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.DeviceUnreachable);
    }

    public void Dispose() => _harness.Dispose();
}
