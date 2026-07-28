using System.Reflection;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// NFR1/AC1/AC2: the read-only safety boundary is double-enforced in the transport. The Redfish allowlist
/// rejects any non-GET, any <c>/Actions/</c> or <c>/Settings</c> path and anything off the allowed prefix
/// set before any HTTP call; the IPMI runner rejects any non-allowlisted subcommand before any process
/// spawn; and a reflection guard proves no driver method carries a mutation verb.
/// </summary>
public sealed class SafetyBoundaryTests
{
    private static readonly RedfishConnectionSettings Settings =
        new("192.0.2.1", 443, "user", "pass", TimeSpan.FromSeconds(2));

    private static readonly IpmiConnectionSettings IpmiSettings =
        new("192.0.2.1", 623, "user", "pass", TimeSpan.FromSeconds(2));

    [Theory]
    [InlineData("/redfish/v1")]
    [InlineData("/redfish/v1/Systems")]
    [InlineData("/redfish/v1/Systems/1")]
    [InlineData("/redfish/v1/Systems/1/EthernetInterfaces")]
    [InlineData("/redfish/v1/Systems/1/EthernetInterfaces/1")]
    [InlineData("/redfish/v1/Systems/1/Bios")]
    [InlineData("/redfish/v1/Managers")]
    [InlineData("/redfish/v1/Managers/1")]
    [InlineData("/redfish/v1/Chassis")]
    [InlineData("/redfish/v1/Systems/1?$select=UUID")]
    public void Allowlist_accepts_read_only_gets(string path)
        => RedfishReadPaths.IsReadOnlyGet("GET", path).Should().BeTrue();

    [Theory]
    [InlineData("/redfish/v1/Systems/1/Actions/ComputerSystem.Reset")]
    [InlineData("/redfish/v1/Managers/1/Actions/Manager.Reset")]
    [InlineData("/redfish/v1/Systems/1/Bios/Settings")]
    [InlineData("/redfish/v1/Systems/1/Bios/Settings/Something")]
    [InlineData("/redfish/v1/AccountService/Accounts")]
    [InlineData("/redfish/v1/SessionService/Sessions")]
    [InlineData("/api/v1/Systems")]
    [InlineData("/redfish/v1/SystemsX")]
    public void Allowlist_rejects_actions_settings_and_off_prefix_paths(string path)
        => RedfishReadPaths.IsReadOnlyGet("GET", path).Should().BeFalse();

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Allowlist_rejects_every_non_get_verb(string method)
        => RedfishReadPaths.IsReadOnlyGet(method, "/redfish/v1/Systems").Should().BeFalse();

    [Fact]
    public async Task Client_rejects_an_action_path_before_any_io()
    {
        using var client = new RedfishClient(Settings, NullLogger.Instance);

        var act = () => client.GetAsync("/redfish/v1/Systems/1/Actions/ComputerSystem.Reset", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not on the Redfish read-only allowlist*");
    }

    [Theory]
    [InlineData("chassis power off")]
    [InlineData("power reset")]
    [InlineData("mc reset cold")]
    [InlineData("raw 0x00 0x01")]
    [InlineData("sel clear")]
    [InlineData("user set password")]
    [InlineData("sol activate")]
    public void Ipmi_allowlist_rejects_write_subcommands(string command)
        => IpmiReadCommands.IsReadOnly(Split(command)).Should().BeFalse();

    [Theory]
    [InlineData("mc info")]
    [InlineData("fru print")]
    [InlineData("lan print")]
    [InlineData("lan print 1")]
    [InlineData("sdr elist")]
    [InlineData("sdr type Temperature")]
    [InlineData("chassis status")]
    public void Ipmi_allowlist_accepts_read_subcommands(string command)
        => IpmiReadCommands.IsReadOnly(Split(command)).Should().BeTrue();

    [Fact]
    public async Task Ipmi_runner_rejects_a_write_subcommand_before_spawning_a_process()
    {
        var runner = new ProcessIpmiCommandRunner(NullLogger<ProcessIpmiCommandRunner>.Instance);

        var act = () => runner.RunAsync(Split("chassis power off"), IpmiSettings, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not on the read-only allowlist*");
    }

    [Fact]
    public void No_driver_method_carries_a_mutation_verb()
    {
        var mutationVerbs = new[]
        {
            "Set", "Update", "Create", "Delete", "Reset", "Power", "Write",
            "Configure", "Reboot", "Enable", "Disable", "Mount", "Provision",
        };

        var methods = typeof(RedfishBmcDriver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            mutationVerbs.Should().NotContain(
                verb => method.Name.Contains(verb, StringComparison.Ordinal),
                $"driver method '{method.Name}' must not imply a mutation");
        }
    }

    private static string[] Split(string command)
        => command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
