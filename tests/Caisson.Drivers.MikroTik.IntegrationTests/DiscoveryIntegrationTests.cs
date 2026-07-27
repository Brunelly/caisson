using System.Diagnostics;
using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// AC5: end-to-end discovery against the in-process simulator (or a real CHR when opted in). The driver
/// is always resolved <b>through the DI registry</b> with a version-agnostic descriptor query, proving
/// the story-1 registry fix, and the whole run must stay well within the 5-second budget (NFR2).
/// </summary>
public sealed class DiscoveryIntegrationTests : IClassFixture<RouterOsChrFixture>
{
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(5);

    private readonly RouterOsChrFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DiscoveryIntegrationTests(RouterOsChrFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void Registry_resolves_the_driver_with_a_version_agnostic_query()
    {
        using var provider = BuildProvider();
        var registry = provider.GetRequiredService<ISwitchDriverRegistry>();

        // A query version that matches no registered version still resolves (story-1 fix), to v1.0.0.
        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "9.9.9-anything");
        registry.TryResolve(query, out var factory).Should().BeTrue();
        factory!.Descriptor.DriverVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Discovers_ports_bridge_hosts_and_vlans_within_the_time_budget()
    {
        using var provider = BuildProvider();
        var driver = CreateDriver(provider, "v7");

        var stopwatch = Stopwatch.StartNew();
        var device = await driver.GetDeviceInfoAsync(CancellationToken.None);
        var ports = await driver.GetPortsAsync(CancellationToken.None);
        var lldp = await driver.GetLldpNeighborsAsync(CancellationToken.None);
        var hosts = await driver.GetBridgeHostTableAsync(CancellationToken.None);
        var vlans = await driver.GetVlansAsync(CancellationToken.None);
        stopwatch.Stop();

        device.Success.Should().BeTrue();
        ports.Success.Should().BeTrue();
        ports.Value!.Should().NotBeEmpty("at least one interface/port must be discovered (AC5)");

        // Bridge host table must be a valid structure — may be empty, must not error.
        hosts.Success.Should().BeTrue();
        hosts.Value.Should().NotBeNull();

        lldp.Success.Should().BeTrue();
        vlans.Success.Should().BeTrue();

        stopwatch.Elapsed.Should().BeLessThan(DiscoveryBudget);
        _output.WriteLine($"Discovery completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Empty_lldp_profile_returns_no_error_and_an_empty_list()
    {
        if (_fixture.UsingRealChr)
        {
            return; // Profile-specific: only meaningful against the simulator.
        }

        using var provider = BuildProvider();
        var driver = CreateDriver(provider, "empty-lldp");

        var lldp = await driver.GetLldpNeighborsAsync(CancellationToken.None);

        lldp.Success.Should().BeTrue();
        lldp.Error.Should().BeNull();
        lldp.Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trapping_section_fails_while_the_others_still_succeed()
    {
        if (_fixture.UsingRealChr)
        {
            return; // The failure profile is simulator-only.
        }

        using var provider = BuildProvider();
        var driver = CreateDriver(provider, "failure");

        var ports = await driver.GetPortsAsync(CancellationToken.None);
        var lldp = await driver.GetLldpNeighborsAsync(CancellationToken.None);
        var hosts = await driver.GetBridgeHostTableAsync(CancellationToken.None);
        var vlans = await driver.GetVlansAsync(CancellationToken.None);

        // The LLDP section traps and fails; the others are unaffected — no crash, best-effort results.
        lldp.Success.Should().BeFalse();
        lldp.Error!.Code.Should().Be(DriverErrorCode.ProtocolError);
        ports.Success.Should().BeTrue();
        ports.Value!.Should().NotBeEmpty();
        hosts.Success.Should().BeTrue();
        vlans.Success.Should().BeTrue();
    }

    [Fact]
    public async Task V6_firmware_parses_and_uses_the_legacy_login_handshake()
    {
        if (_fixture.UsingRealChr)
        {
            return; // The v6 fixture pass is simulator-only.
        }

        using var provider = BuildProvider();
        var driver = CreateDriver(provider, "v6");

        // A successful call also proves the pre-6.43 MD5 challenge-response login works end-to-end.
        var device = await driver.GetDeviceInfoAsync(CancellationToken.None);
        var ports = await driver.GetPortsAsync(CancellationToken.None);

        device.Success.Should().BeTrue();
        device.Value!.OsVersion.Should().StartWith("6.");
        device.Value.Serial.Should().Be("7A1B0ABCDEF");
        ports.Value!.Single(p => p.PortName == "ether1").IsUp.Should().BeTrue();
    }

    private ISwitchDiscoveryDriver CreateDriver(ServiceProvider provider, string profile)
    {
        var registry = provider.GetRequiredService<ISwitchDriverRegistry>();
        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "0.0.0-any");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        var endpoint = _fixture.ResolveEndpoint(profile);
        var options = new SwitchConnectionOptions(endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(2), "core-switch");
        return factory!.Create(options);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new XunitLoggerFactory(_output));
        // Registered before AddMikroTik... so its TryAdd does not override our test-scoped resolver.
        services.AddSingleton<ISwitchCredentialResolver>(_ => new EnvSwitchCredentialResolver(_fixture.EnvLookup));
        services.AddMikroTikRouterOsSwitchDriver();
        services.AddCaissonDriverRegistry();
        return services.BuildServiceProvider();
    }
}
