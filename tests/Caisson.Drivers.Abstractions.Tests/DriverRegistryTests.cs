using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Tests.Mocks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// AC1: a driver factory registered via <c>AddSwitchDriver&lt;T&gt;</c>/<c>AddBmcDriver&lt;T&gt;</c>
/// against a real <see cref="ServiceCollection"/> can be resolved through the registry using its
/// <see cref="DriverDescriptor"/>, without business logic referencing the concrete factory type.
/// </summary>
public sealed class DriverRegistryTests
{
    private static readonly DriverDescriptor MockSwitchDescriptor =
        new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    private static readonly DriverDescriptor MockBmcDescriptor =
        new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    [Fact]
    public void Switch_driver_factory_resolves_by_descriptor_through_di()
    {
        var services = new ServiceCollection();
        services.AddSwitchDriver<MockSwitchDriverFactory>();
        services.AddCaissonDriverRegistry();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ISwitchDriverRegistry>();

        registry.TryResolve(MockSwitchDescriptor, out var factory).Should().BeTrue();
        factory!.Descriptor.Should().Be(MockSwitchDescriptor);
        registry.RegisteredDrivers.Should().Contain(MockSwitchDescriptor);
    }

    [Fact]
    public void Bmc_driver_factory_resolves_by_descriptor_through_di()
    {
        var services = new ServiceCollection();
        services.AddBmcDriver<MockBmcDriverFactory>();
        services.AddCaissonDriverRegistry();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IBmcDriverRegistry>();

        registry.TryResolve(MockBmcDescriptor, out var factory).Should().BeTrue();
        factory!.Descriptor.Should().Be(MockBmcDescriptor);
    }

    [Fact]
    public void TryResolve_returns_false_for_an_unregistered_descriptor()
    {
        var registry = new SwitchDriverRegistry(Array.Empty<ISwitchDriverFactory>());

        var resolved = registry.TryResolve(MockSwitchDescriptor, out var factory);

        resolved.Should().BeFalse();
        factory.Should().BeNull();
    }

    [Fact]
    public void Registering_two_factories_with_an_identical_descriptor_throws_at_construction()
    {
        var first = new MockSwitchDriverFactory();
        var second = new MockSwitchDriverFactory();

        var act = () => new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)first, second });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Bmc_registry_also_throws_on_duplicate_descriptor()
    {
        var first = new MockBmcDriverFactory();
        var second = new MockBmcDriverFactory();

        var act = () => new BmcDriverRegistry(new[] { (IBmcDriverFactory)first, second });

        act.Should().Throw<InvalidOperationException>();
    }
}
