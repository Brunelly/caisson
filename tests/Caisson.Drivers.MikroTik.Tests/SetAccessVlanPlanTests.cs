using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC2/NFR3: a dry-run computes and returns the intended plan without changing device state, and
/// setting a port to its already-current VLAN is a no-op that sends zero mutating commands.
/// </summary>
public sealed class SetAccessVlanPlanTests
{
    private static RouterOsSwitchMutatingDriver DriverFor(FakeRouterOsWriteApiClient client)
        => new("10.0.0.1", () => client, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            new RouterOsWriteMetrics(), TimeProvider.System, new CapturingLogger<RouterOsSwitchMutatingDriver>());

    private static SetAccessVlanRequest Request(string port, int vlanId, bool dryRun)
        => new(port, vlanId, dryRun, ConfirmWindow: null, Guid.NewGuid(), "operator@example.com", ActorType.User);

    private static FakeRouterOsWriteApiClient ClientWithPort(string port, int currentPvid, params int[] configuredVlanIds)
    {
        var client = new FakeRouterOsWriteApiClient();
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", port), ("pvid", currentPvid.ToString())),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, configuredVlanIds.Select(id =>
            RouterOsFixtures.Row(("vlan-ids", id.ToString()), ("untagged", id == currentPvid ? port : string.Empty))).ToArray());
        return client;
    }

    [Fact]
    public async Task Dry_run_returns_the_intended_plan_and_sends_only_read_commands()
    {
        var client = ClientWithPort("ether1", currentPvid: 10, 10, 20);
        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20, dryRun: true), CancellationToken.None);

        result.Success.Should().BeTrue();
        var outcome = result.Value!;
        outcome.ReasonCode.Should().Be(SwitchChangeReasonCode.DryRunPlanned);
        outcome.Before!.Pvid.Should().Be(10);
        outcome.After!.Pvid.Should().Be(20);
        outcome.Confirmed.Should().BeFalse();
        outcome.Plan.Steps.Should().ContainSingle(s => s is BridgePortPvidChange).Which
            .Should().BeOfType<BridgePortPvidChange>().Which.ToVlanId.Should().Be(20);

        client.SentCommands.Should().OnlyContain(c => c.EndsWith("/print", StringComparison.Ordinal));
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.BridgePortSet);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.SchedulerAdd);
    }

    [Fact]
    public async Task Idempotent_desired_equal_to_current_is_a_noop_with_zero_mutating_commands()
    {
        var client = ClientWithPort("ether1", currentPvid: 10, 10);
        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 10, dryRun: false), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.NoOpAlreadyDesiredState);
        result.Value.Plan.Steps.Should().BeEmpty();

        client.SentCommands.Should().NotContain(RouterOsWriteCommands.BridgePortSet);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.SchedulerAdd);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.SchedulerRemove);
    }
}
