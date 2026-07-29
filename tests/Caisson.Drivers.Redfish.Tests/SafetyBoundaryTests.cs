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

    [Theory]
    // A device-supplied @odata.id must not escape the allowlisted subtree via "." / ".." dot-segments:
    // HttpClient/Uri collapse these before the request is sent, so validating the raw string would otherwise
    // admit a path that resolves to an off-allowlist resource such as /redfish/v1/AccountService.
    [InlineData("/redfish/v1/Systems/../AccountService")]
    [InlineData("/redfish/v1/Systems/1/../../AccountService")]
    [InlineData("/redfish/v1/Managers/./../SessionService")]
    [InlineData("/redfish/v1/Systems/..")]
    [InlineData("/redfish/v1/Systems/./1")]
    [InlineData("/redfish/v1/Systems/1/EthernetInterfaces/../../../SessionService")]
    // Percent-encoded dot-segments are rejected too (decoded before the segment check).
    [InlineData("/redfish/v1/Systems/%2e%2e/AccountService")]
    [InlineData("/redfish/v1/Systems/%2E%2E/AccountService")]
    // Backslash-hidden dot-segments: a single '/'-split segment like "..\..\AccountService" hides the
    // traversal, but .NET's Uri normalizes '\' to '/', collapsing it to an off-allowlist resource.
    [InlineData("/redfish/v1/Systems/..\\..\\AccountService")]
    [InlineData("/redfish/v1/Systems/1/..\\..\\SessionService")]
    [InlineData("/redfish/v1/Systems\\..\\AccountService")]
    [InlineData("/redfish/v1/Systems/1/%5c..%5c..%5cAccountService")]
    public void Allowlist_rejects_dot_segment_traversal(string path)
        => RedfishReadPaths.IsReadOnlyGet("GET", path).Should().BeFalse();

    [Fact]
    public void Allowlist_rejects_a_path_carrying_a_crlf_log_injection_payload()
        => RedfishReadPaths.IsReadOnlyGet("GET", "/redfish/v1/Systems/1\r\nx").Should().BeFalse();

    [Fact]
    public void Allowlist_rejects_a_path_over_the_length_cap()
        => RedfishReadPaths.IsReadOnlyGet("GET", "/redfish/v1/Systems/" + new string('1', 600)).Should().BeFalse();

    [Fact]
    public void SanitizeForLog_strips_control_characters_and_truncates()
    {
        var sanitized = RedfishReadPaths.SanitizeForLog("/redfish/v1/Systems/1\r\nInjected: header" + new string('x', 500));

        sanitized.Should().NotContain("\r").And.NotContain("\n");
        sanitized.Length.Should().BeLessThanOrEqualTo(280);
    }

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
    public async Task Ipmi_runner_reports_unavailable_rather_than_crashing_when_the_configured_path_does_not_exist()
    {
        var runner = new ProcessIpmiCommandRunner(NullLogger<ProcessIpmiCommandRunner>.Instance, "/nonexistent/ipmitool");

        var result = await runner.RunAsync(Split("mc info"), IpmiSettings, CancellationToken.None);

        result.Available.Should().BeFalse();
    }

    [Fact]
    public async Task Ipmi_runner_reports_unavailable_rather_than_crashing_when_the_configured_path_is_world_writable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file-mode check only.
        }

        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.OtherWrite);
            var runner = new ProcessIpmiCommandRunner(NullLogger<ProcessIpmiCommandRunner>.Instance, path);

            var result = await runner.RunAsync(Split("mc info"), IpmiSettings, CancellationToken.None);

            result.Available.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
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
