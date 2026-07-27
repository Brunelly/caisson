using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC2/AC3/NFR3: expected failures are mapped to <see cref="DriverError"/> codes (never thrown), a
/// single failed section is isolated from the others, and caller cancellation surfaces as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class ErrorMappingTests
{
    private readonly RouterOsMetrics _metrics = new();

    private RouterOsSwitchDriver DriverFor(FakeRouterOsApiClient client)
        => new("10.0.0.1", () => client, _metrics, new CapturingLogger<RouterOsSwitchDriver>());

    [Fact]
    public async Task Auth_failure_maps_to_authentication_failed_and_is_not_retryable()
    {
        var client = new FakeRouterOsApiClient
        {
            OnConnect = () => throw new RouterOsAuthenticationException("RouterOS rejected the supplied credentials."),
        };

        var result = await DriverFor(client).GetDeviceInfoAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.AuthenticationFailed);
        result.Error.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Timeout_maps_to_connection_timeout_and_is_retryable()
    {
        var client = new FakeRouterOsApiClient();
        client.SetThrows(RouterOsReadCommands.Interfaces, () => new TimeoutException("timed out"));

        var result = await DriverFor(client).GetPortsAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ConnectionTimeout);
        result.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_single_failing_section_does_not_prevent_the_others_from_succeeding()
    {
        var client = new FakeRouterOsApiClient();
        client.SetRows(RouterOsReadCommands.Interfaces, RouterOsFixtures.V7.Interfaces);
        client.SetThrows(RouterOsReadCommands.IpNeighbors, () => new RouterOsApiException("no such command"));

        var driver = DriverFor(client);

        var lldp = await driver.GetLldpNeighborsAsync(CancellationToken.None);
        var ports = await driver.GetPortsAsync(CancellationToken.None);

        lldp.Success.Should().BeFalse();
        lldp.Error!.Code.Should().Be(DriverErrorCode.ProtocolError);
        ports.Success.Should().BeTrue();
        ports.Value!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_auxiliary_section_failure_within_a_call_degrades_to_a_diagnostic()
    {
        var client = new FakeRouterOsApiClient();
        client.SetRows(RouterOsReadCommands.Interfaces, RouterOsFixtures.V7.Interfaces);
        client.SetThrows(RouterOsReadCommands.BridgeVlans, () => new RouterOsApiException("boom"));

        var result = await DriverFor(client).GetPortsAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.Should().NotBeEmpty();
        result.Diagnostics.Should().Contain(d => d.EntityRef == "bridge-vlan");
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_operation_cancelled()
    {
        var client = new FakeRouterOsApiClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => DriverFor(client).GetPortsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
