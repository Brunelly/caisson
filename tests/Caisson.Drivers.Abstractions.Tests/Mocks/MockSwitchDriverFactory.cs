using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;

namespace Caisson.Drivers.Abstractions.Tests.Mocks;

/// <summary>A configurable in-memory <see cref="ISwitchDriverFactory"/> for unit tests.</summary>
public sealed class MockSwitchDriverFactory : ISwitchDriverFactory
{
    /// <inheritdoc />
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    /// <summary>Overridable driver creation, defaulting to a fresh <see cref="MockSwitchDiscoveryDriver"/>.</summary>
    public Func<SwitchConnectionOptions, ISwitchDiscoveryDriver> DriverFactory { get; set; } =
        _ => new MockSwitchDiscoveryDriver();

    /// <inheritdoc />
    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options) => DriverFactory(options);
}
