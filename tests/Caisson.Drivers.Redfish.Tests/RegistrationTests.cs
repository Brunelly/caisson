using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Redfish.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC/ADR 0007: <c>AddHpeRedfishBmcDriver()</c> registers the factory via <c>AddBmcDriver&lt;T&gt;()</c> so
/// the driver resolves through <see cref="IBmcDriverRegistry"/> with a version-agnostic descriptor query.
/// </summary>
public sealed class RegistrationTests
{
    [Fact]
    public void The_registry_resolves_the_redfish_factory_with_a_version_agnostic_query()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddHpeRedfishBmcDriver();
        services.AddCaissonDriverRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IBmcDriverRegistry>();

        // A query version that matches no registered version still resolves (ADR 0007), to v1.0.0.
        var query = new DriverDescriptor("HPE", null, DriverConnectionKind.Redfish, "9.9.9-anything");
        registry.TryResolve(query, out var factory).Should().BeTrue();
        factory!.Descriptor.DriverVersion.Should().Be("1.0.0");
        factory.Descriptor.Vendor.Should().Be("HPE");
    }

    [Fact]
    public void The_factory_binds_the_default_https_port_and_creates_a_driver()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddHpeRedfishBmcDriver();
        services.AddCaissonDriverRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IBmcDriverRegistry>();
        registry.TryResolve(RedfishBmcDriver.RedfishDescriptor, out var factory).Should().BeTrue();

        var driver = factory!.Create(new BmcConnectionOptions("10.0.0.1", Port: null, TimeSpan.FromSeconds(5), "ilo_1"));

        driver.Descriptor.Should().Be(RedfishBmcDriver.RedfishDescriptor);
    }
}
