using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// NFR1: the write allowlist is a SEPARATE, exact, bounded set from the read-only allowlist, and
/// <see cref="RouterOsWriteApiClient.ExecuteAsync"/> rejects anything off it before any socket I/O —
/// mirroring <see cref="SafetyBoundaryTests"/> for the write transport. Also a regression guard that this
/// story never widened <see cref="RouterOsReadCommands.Allowlist"/>.
/// </summary>
public sealed class WriteSafetyBoundaryTests
{
    private static readonly RouterOsConnectionSettings Settings =
        new("192.0.2.1", 8728, UseTls: false, "user", "pass", TimeSpan.FromSeconds(2));

    [Fact]
    public void Write_allowlist_is_exactly_the_six_intended_commands()
    {
        RouterOsWriteCommands.Allowlist.Should().BeEquivalentTo(new[]
        {
            "/interface/bridge/port/print",
            "/interface/bridge/port/set",
            "/interface/bridge/vlan/print",
            "/system/scheduler/print",
            "/system/scheduler/add",
            "/system/scheduler/remove",
        });
    }

    [Fact]
    public void Write_allowlist_is_disjoint_in_intent_from_dangerous_out_of_scope_commands()
    {
        var dangerous = new[]
        {
            "/system/reboot", "/system/reset-configuration", "/user/add", "/user/set",
            "/ip/firewall/filter/add", "/system/script/run",
        };

        foreach (var command in dangerous)
        {
            RouterOsWriteCommands.Allowlist.Should().NotContain(command);
        }
    }

    [Theory]
    [InlineData("/system/reboot")]
    [InlineData("/user/add")]
    [InlineData("/system/reset-configuration")]
    [InlineData("/interface/bridge/vlan/add")]
    [InlineData(RouterOsReadCommands.Interfaces)]
    public async Task ExecuteAsync_rejects_an_off_allowlist_command_before_any_io(string command)
    {
        // The client is never connected — an off-allowlist command must be rejected by the allowlist
        // guard that runs before the "not connected" check and any socket I/O.
        await using var client = new RouterOsWriteApiClient(Settings, NullLogger.Instance);

        var act = () => client.ExecuteAsync(command, Array.Empty<string>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not on the RouterOS write allowlist*");
    }

    [Fact]
    public async Task ExecuteAsync_for_an_allowlisted_command_while_disconnected_reports_a_connect_error()
    {
        await using var client = new RouterOsWriteApiClient(Settings, NullLogger.Instance);

        var act = () => client.ExecuteAsync(RouterOsWriteCommands.BridgePortPrint, Array.Empty<string>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
    }

    [Fact]
    public void Read_allowlist_is_unchanged_by_this_story_and_remains_print_only()
    {
        // Regression guard (ADR 0031): the write story must never widen the read-only allowlist.
        RouterOsReadCommands.Allowlist.Should().NotBeEmpty();
        RouterOsReadCommands.Allowlist.Should().OnlyContain(command => command.EndsWith("/print", StringComparison.Ordinal));
        RouterOsReadCommands.Allowlist.Should().NotIntersectWith(new[] { RouterOsWriteCommands.BridgePortSet, RouterOsWriteCommands.SchedulerAdd, RouterOsWriteCommands.SchedulerRemove });
    }
}
