using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;

namespace Caisson.Drivers.Abstractions.Tests.Mocks;

/// <summary>A configurable in-memory <see cref="IBmcDriverFactory"/> for unit tests.</summary>
public sealed class MockBmcDriverFactory : IBmcDriverFactory
{
    /// <inheritdoc />
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    /// <summary>Overridable driver creation, defaulting to a fresh <see cref="MockBmcDiscoveryDriver"/>.</summary>
    public Func<BmcConnectionOptions, IBmcDiscoveryDriver> DriverFactory { get; set; } =
        _ => new MockBmcDiscoveryDriver();

    /// <inheritdoc />
    public IBmcDiscoveryDriver Create(BmcConnectionOptions options) => DriverFactory(options);
}
