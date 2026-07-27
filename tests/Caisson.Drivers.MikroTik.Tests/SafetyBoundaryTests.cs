using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// NFR1/AC1: the read-only safety boundary is enforced in the transport. The allowlist is print-only,
/// and <see cref="RouterOsApiClient.SendCommandAsync"/> rejects mutating commands before any socket I/O.
/// </summary>
public sealed class SafetyBoundaryTests
{
    private static readonly RouterOsConnectionSettings Settings =
        new("192.0.2.1", 8728, UseTls: false, "user", "pass", TimeSpan.FromSeconds(2));

    [Fact]
    public void Allowlist_contains_only_print_commands_and_no_write_verbs()
    {
        RouterOsReadCommands.Allowlist.Should().NotBeEmpty();
        RouterOsReadCommands.Allowlist.Should().OnlyContain(command => command.EndsWith("/print", StringComparison.Ordinal));

        var mutationVerbs = new[] { "set", "add", "remove", "reboot", "enable", "disable", "reset", "power" };
        foreach (var command in RouterOsReadCommands.Allowlist)
        {
            mutationVerbs.Should().NotContain(verb => command.Contains("/" + verb, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("/interface/set")]
    [InlineData("/interface/bridge/vlan/add")]
    [InlineData("/ip/address/add")]
    [InlineData("/system/reboot")]
    public async Task SendCommandAsync_rejects_a_mutating_command_before_any_io(string mutatingCommand)
    {
        // The client is never connected — a mutating command must be rejected by the allowlist guard
        // that runs before the "not connected" check and any socket I/O.
        await using var client = new RouterOsApiClient(Settings, NullLogger.Instance);

        var act = () => client.SendCommandAsync(mutatingCommand, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not on the RouterOS read-only allowlist*");
    }

    [Fact]
    public async Task SendCommandAsync_for_an_allowlisted_command_while_disconnected_reports_a_connect_error()
    {
        // An allowlisted command passes the guard, then fails the connection check — proving the guard
        // is specifically about the command, not the connection state.
        await using var client = new RouterOsApiClient(Settings, NullLogger.Instance);

        var act = () => client.SendCommandAsync(RouterOsReadCommands.Interfaces, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
    }
}
