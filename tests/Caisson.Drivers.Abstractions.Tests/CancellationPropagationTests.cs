using Caisson.Drivers.Abstractions.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// NFR2/AC: cancellation is caller-initiated control flow, not a device-reported failure, so an
/// already-cancelled token must surface as a thrown <see cref="OperationCanceledException"/> per BCL
/// convention rather than a failed <see cref="Results.DriverResult{T}"/> (see ADR 0006).
/// </summary>
public sealed class CancellationPropagationTests
{
    [Fact]
    public async Task Switch_driver_throws_for_an_already_cancelled_token()
    {
        var driver = new MockSwitchDiscoveryDriver();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => driver.GetPortsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Bmc_driver_throws_for_an_already_cancelled_token()
    {
        var driver = new MockBmcDiscoveryDriver();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => driver.GetNetworkInterfacesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
