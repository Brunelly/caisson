using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC3/AC4: covers every <see cref="SwitchChangeReasonCode"/> reachable from
/// <see cref="RouterOsSwitchMutatingDriver.SetAccessVlanAsync"/> — invalid input rejected before any I/O,
/// port-not-found/ambiguous-port/VLAN-not-configured fail fast, a happy apply arms→sets→verifies→confirms
/// in order, a verification mismatch withholds confirm, and an infrastructure (connect) failure maps to
/// <see cref="DriverResult{T}.Fail"/> rather than a domain outcome.
/// </summary>
public sealed class SetAccessVlanReasonCodeTests
{
    private static RouterOsSwitchMutatingDriver DriverFor(FakeRouterOsWriteApiClient client)
        => new("10.0.0.1", () => client, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            new RouterOsWriteMetrics(), TimeProvider.System, new CapturingLogger<RouterOsSwitchMutatingDriver>());

    private static SetAccessVlanRequest Request(string port, int vlanId)
        => new(port, vlanId, DryRun: false, ConfirmWindow: null, Guid.NewGuid(), "operator@example.com", ActorType.User);

    [Fact]
    public async Task Invalid_vlan_id_is_rejected_before_any_device_io()
    {
        var client = new FakeRouterOsWriteApiClient();

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 4095), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.InvalidVlanId);
        client.ConnectCount.Should().Be(0, "an out-of-range VLAN id must never reach the device (NFR1)");
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Port_not_found_reports_the_reason_code_and_sends_no_mutating_commands()
    {
        var client = new FakeRouterOsWriteApiClient();
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, Array.Empty<IReadOnlyDictionary<string, string>>());

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether9", 20), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.PortNotFound);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.BridgePortSet);
    }

    [Fact]
    public async Task Ambiguous_port_match_fails_fast_without_guessing()
    {
        var client = new FakeRouterOsWriteApiClient();
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", "10")),
            RouterOsFixtures.Row((".id", "*2"), ("interface", "ether1"), ("pvid", "10")),
        });

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.AmbiguousPort);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.BridgePortSet);
    }

    [Fact]
    public async Task Target_vlan_not_configured_on_the_bridge_is_rejected()
    {
        var client = new FakeRouterOsWriteApiClient();
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", "10")),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, new[]
        {
            RouterOsFixtures.Row(("vlan-ids", "10"), ("untagged", "ether1")),
        });

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.VlanNotConfigured);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.BridgePortSet);
        client.SentCommands.Should().NotContain(RouterOsWriteCommands.SchedulerAdd);
    }

    [Fact]
    public async Task Happy_apply_arms_then_sets_then_verifies_then_confirms_in_order()
    {
        var client = new FakeRouterOsWriteApiClient();
        var currentPvid = 10;

        client.SetHandler(RouterOsWriteCommands.BridgePortPrint, _ => new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", currentPvid.ToString())),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, new[]
        {
            RouterOsFixtures.Row(("vlan-ids", "10"), ("untagged", "ether1")),
            RouterOsFixtures.Row(("vlan-ids", "20"), ("untagged", string.Empty)),
        });
        client.SetHandler(RouterOsWriteCommands.BridgePortSet, words =>
        {
            var pvidWord = words.Single(w => w.StartsWith("=pvid=", StringComparison.Ordinal));
            currentPvid = int.Parse(pvidWord["=pvid=".Length..]);
            return Array.Empty<IReadOnlyDictionary<string, string>>();
        });

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeTrue();
        var outcome = result.Value!;
        outcome.ReasonCode.Should().Be(SwitchChangeReasonCode.Applied);
        outcome.Confirmed.Should().BeTrue();
        outcome.Verification!.Verified.Should().BeTrue();
        outcome.Before!.Pvid.Should().Be(10);
        outcome.After!.Pvid.Should().Be(20);

        client.SentCommands.Should().Equal(
            RouterOsWriteCommands.BridgePortPrint,
            RouterOsWriteCommands.BridgeVlanPrint,
            RouterOsWriteCommands.SchedulerAdd,
            RouterOsWriteCommands.BridgePortSet,
            RouterOsWriteCommands.BridgePortPrint,
            RouterOsWriteCommands.SchedulerRemove);

        // The armed rollback's on-event carries only the validated port name and observed before-PVID.
        var schedulerAddWords = client.Calls.Single(c => c.Command == RouterOsWriteCommands.SchedulerAdd).Words;
        schedulerAddWords.Should().Contain(w => w.StartsWith("=on-event=", StringComparison.Ordinal)
            && w.Contains("interface=\"ether1\"", StringComparison.Ordinal)
            && w.Contains("pvid=10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verification_mismatch_withholds_confirm_and_reports_verification_failed()
    {
        var client = new FakeRouterOsWriteApiClient();

        // The port print always reports pvid=10 — simulating a /set that silently failed to take effect.
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", "10")),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, new[]
        {
            RouterOsFixtures.Row(("vlan-ids", "10"), ("untagged", "ether1")),
            RouterOsFixtures.Row(("vlan-ids", "20"), ("untagged", string.Empty)),
        });

        var driver = DriverFor(client);
        var pending = await driver.BeginChangeAsync(Request("ether1", 20), CancellationToken.None);

        pending.Result.Success.Should().BeTrue();
        pending.Result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.VerificationFailed);
        pending.Result.Value.Confirmed.Should().BeFalse();
        pending.Client.Should().BeNull("a failed verification must not leave a client open for a confirm the caller should never send");

        client.SentCommands.Should().NotContain(RouterOsWriteCommands.SchedulerRemove);
    }

    [Fact]
    public async Task Connect_failure_maps_to_an_infrastructure_error_not_a_domain_outcome()
    {
        var client = new FakeRouterOsWriteApiClient
        {
            OnConnect = () => throw new RouterOsAuthenticationException("RouterOS rejected the supplied credentials."),
        };

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.AuthenticationFailed);
        result.Error.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Timeout_maps_to_connection_timeout_and_is_retryable()
    {
        var client = new FakeRouterOsWriteApiClient();
        client.SetThrows(RouterOsWriteCommands.BridgePortPrint, () => new TimeoutException("timed out"));

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ConnectionTimeout);
        result.Error.Retryable.Should().BeTrue();
    }
}
