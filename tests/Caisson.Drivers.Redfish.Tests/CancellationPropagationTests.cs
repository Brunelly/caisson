using Caisson.Drivers.Redfish.Tests.Fakes;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC2/ADR 0006: only caller cancellation surfaces as <see cref="OperationCanceledException"/>; every other
/// expected failure comes back as a structured result. This mirrors the switch driver's contract.
/// </summary>
public sealed class CancellationPropagationTests : IDisposable
{
    private readonly RedfishDriverHarness _harness = new();

    public static IEnumerable<object[]> Methods()
    {
        yield return new object[] { "inventory" };
        yield return new object[] { "network" };
        yield return new object[] { "bios" };
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task An_already_cancelled_token_throws_operation_cancelled(string method)
    {
        var driver = _harness.Build(Fixtures.RedfishFixtures.SuccessClient(), new StubIpmiCommandRunner());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = method switch
        {
            "inventory" => () => driver.GetSystemInventoryAsync(cts.Token),
            "network" => () => driver.GetNetworkInterfacesAsync(cts.Token),
            _ => () => driver.GetBiosInfoAsync(cts.Token),
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose() => _harness.Dispose();
}
