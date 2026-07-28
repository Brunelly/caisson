using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// Builds a <see cref="RedfishBmcDriver"/> over a <see cref="FakeRedfishClient"/> and
/// <see cref="StubIpmiCommandRunner"/> for the driver-level tests, owning the shared
/// <see cref="RedfishMetrics"/> so each test class can dispose it.
/// </summary>
public sealed class RedfishDriverHarness : IDisposable
{
    private const string Host = "10.4.7.5";

    private static readonly IpmiConnectionSettings Ipmi =
        new(Host, 623, "reader", "pass", TimeSpan.FromSeconds(5));

    private readonly RedfishMetrics _metrics = new();

    public RedfishBmcDriver Build(
        FakeRedfishClient client,
        StubIpmiCommandRunner runner,
        ILogger<RedfishBmcDriver>? logger = null,
        Func<IpmiConnectionSettings>? ipmiSettings = null)
        => new(
            Host,
            () => client,
            () => runner,
            ipmiSettings ?? (() => Ipmi),
            TimeSpan.FromSeconds(5),
            _metrics,
            logger ?? NullLogger<RedfishBmcDriver>.Instance);

    public void Dispose() => _metrics.Dispose();
}
