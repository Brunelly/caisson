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

    // ADR 0007: resolution matches (Vendor, Model, ConnectionKind) and ignores the query's
    // DriverVersion, selecting the highest registered version when several match.

    [Fact]
    public void Registering_two_versions_for_the_same_key_does_not_throw()
    {
        var v1 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0") };
        var v2 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "2.0.0") };

        var act = () => new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)v1, v2 });

        act.Should().NotThrow();
    }

    [Fact]
    public void TryResolve_selects_the_highest_version_when_several_match()
    {
        var v1 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0") };
        var v2 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "2.0.0") };
        var registry = new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)v1, v2 });

        // Query carries a version that matches neither registered descriptor exactly.
        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "9.9.9-query");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        factory!.Descriptor.DriverVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void TryResolve_ignores_the_query_version_when_a_single_driver_is_registered()
    {
        var registered = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0") };
        var registry = new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)registered });

        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "does-not-matter");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        factory!.Descriptor.Should().Be(registered.Descriptor);
    }

    [Fact]
    public void TryResolve_orders_versions_numerically_not_lexicographically()
    {
        var v1_9 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.9.0") };
        var v1_10 = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.10.0") };
        var registry = new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)v1_9, v1_10 });

        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "0.0.0");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        // Lexicographically "1.10.0" < "1.9.0"; numerically 1.10.0 is the newer release.
        factory!.Descriptor.DriverVersion.Should().Be("1.10.0");
    }

    [Fact]
    public void TryResolve_does_not_match_a_different_connection_kind()
    {
        var registered = new MockSwitchDriverFactory { Descriptor = new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0") };
        var registry = new SwitchDriverRegistry(new[] { (ISwitchDriverFactory)registered });

        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.Ssh, "1.0.0");

        registry.TryResolve(query, out var factory).Should().BeFalse();
        factory.Should().BeNull();
    }

    [Fact]
    public void Bmc_registry_also_resolves_the_highest_version_agnostic_of_the_query_version()
    {
        var v1 = new MockBmcDriverFactory { Descriptor = new("Dell", null, DriverConnectionKind.Redfish, "1.0.0") };
        var v2 = new MockBmcDriverFactory { Descriptor = new("Dell", null, DriverConnectionKind.Redfish, "2.0.0") };
        var registry = new BmcDriverRegistry(new[] { (IBmcDriverFactory)v1, v2 });

        var query = new DriverDescriptor("Dell", null, DriverConnectionKind.Redfish, "7.7.7-query");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        factory!.Descriptor.DriverVersion.Should().Be("2.0.0");
    }
}
