using System.Diagnostics;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// AC1–AC4: end-to-end discovery against the in-process HTTPS Redfish simulator (or a real iLO when opted
/// in). The driver is always resolved <b>through the DI registry</b> with a version-agnostic descriptor
/// query, proving ADR 0007 for BMC drivers, and the whole run stays well within the 5-second budget (NFR2).
/// </summary>
public sealed class DiscoveryIntegrationTests : IClassFixture<RedfishBmcFixture>
{
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, string> AllIpmiFixtures = new Dictionary<string, string>
    {
        ["mc info"] = "ipmi-mc-info.txt",
        ["fru print"] = "ipmi-fru-print.txt",
        ["lan print"] = "ipmi-lan-print.txt",
    };

    private readonly RedfishBmcFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DiscoveryIntegrationTests(RedfishBmcFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void Registry_resolves_the_driver_with_a_version_agnostic_query()
    {
        using var provider = BuildProvider(new FixtureIpmiCommandRunner(AllIpmiFixtures));
        var registry = provider.GetRequiredService<IBmcDriverRegistry>();

        var query = new DriverDescriptor("HPE", null, DriverConnectionKind.Redfish, "9.9.9-anything");
        registry.TryResolve(query, out var factory).Should().BeTrue();
        factory!.Descriptor.DriverVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Discovers_identity_nics_and_bios_within_the_time_budget()
    {
        using var provider = BuildProvider(new FixtureIpmiCommandRunner(AllIpmiFixtures));
        var driver = CreateDriver(provider, _fixture.ResolveEndpoint("ilo-success"));

        var stopwatch = Stopwatch.StartNew();
        var inventory = await driver.GetSystemInventoryAsync(CancellationToken.None);
        var nics = await driver.GetNetworkInterfacesAsync(CancellationToken.None);
        var bios = await driver.GetBiosInfoAsync(CancellationToken.None);
        stopwatch.Stop();

        inventory.Success.Should().BeTrue();
        nics.Success.Should().BeTrue();
        bios.Success.Should().BeTrue();

        if (!_fixture.UsingRealHardware)
        {
            inventory.Value!.BmcUuid.Should().Be("38373035-3831-4247-3830-353531384752");
            inventory.Value.Serial.Should().Be("CZ3629abcd");
            inventory.Value.Hostname.Should().Be("esx-node-07");

            nics.Value!.Should().HaveCount(2);
            nics.Value!.Select(n => n.Mac!.Value.Value).Should().BeEquivalentTo(new[] { "001a2b3c4d5e", "001a2b3c4d5f" });

            bios.Value!.Version.Should().Be("U30 v2.60");
        }

        stopwatch.Elapsed.Should().BeLessThan(DiscoveryBudget);
        _output.WriteLine($"Discovery completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Missing_identity_still_succeeds_with_a_degraded_warning()
    {
        if (_fixture.UsingRealHardware)
        {
            return; // Profile-specific: simulator only.
        }

        using var provider = BuildProvider(new FixtureIpmiCommandRunner(AllIpmiFixtures));
        var driver = CreateDriver(provider, _fixture.ResolveEndpoint("ilo-missing-serial"));

        var inventory = await driver.GetSystemInventoryAsync(CancellationToken.None);

        inventory.Success.Should().BeTrue();
        inventory.Value!.Serial.Should().BeNull();
        inventory.Diagnostics.Should().Contain(d => d.Message.Contains("degraded"));
    }

    [Fact]
    public async Task Auth_failure_maps_to_authentication_failed()
    {
        if (_fixture.UsingRealHardware)
        {
            return; // The 401 profile is simulator-only.
        }

        // No IPMI fixtures, so the auth failure is not masked by a successful fallback.
        using var provider = BuildProvider(new FixtureIpmiCommandRunner(new Dictionary<string, string>()));
        var driver = CreateDriver(provider, _fixture.ResolveEndpoint("ilo-auth-fail"));

        var inventory = await driver.GetSystemInventoryAsync(CancellationToken.None);

        inventory.Success.Should().BeFalse();
        inventory.Error!.Code.Should().Be(DriverErrorCode.AuthenticationFailed);
        inventory.Error.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Redfish_unreachable_falls_back_to_ipmi_with_provenance()
    {
        if (_fixture.UsingRealHardware)
        {
            return; // Simulator-only unreachable scenario.
        }

        var runner = new FixtureIpmiCommandRunner(AllIpmiFixtures);
        using var provider = BuildProvider(runner);
        var driver = CreateDriver(provider, _fixture.UnreachableEndpoint());

        var inventory = await driver.GetSystemInventoryAsync(CancellationToken.None);

        inventory.Success.Should().BeTrue("the IPMI fallback recovered the inventory");
        inventory.Value!.Serial.Should().Be("CZ3629abcd");
        inventory.Diagnostics.Should().Contain(d => d.ReasonCode == ReasonCode.FallbackSource);
        runner.Invocations.Should().Contain("fru print");
    }

    [Fact]
    public async Task A_device_supplied_traversal_link_is_refused_and_never_requested()
    {
        if (_fixture.UsingRealHardware)
        {
            return; // The malicious-BMC profile is simulator-only.
        }

        // No IPMI fixtures — the discovery must fail outright, not be quietly rescued by a fallback.
        using var provider = BuildProvider(new FixtureIpmiCommandRunner(new Dictionary<string, string>()));
        var simulator = _fixture.ResolveSimulator("ilo-malicious-traversal");
        var driver = CreateDriver(provider, new RedfishEndpoint(simulator.Host, simulator.Port));

        // The Systems collection hands back an @odata.id of "/redfish/v1/Systems/../AccountService". Without the
        // dot-segment reject, HttpClient would canonicalize that to "/redfish/v1/AccountService" and leak it.
        var inventory = await driver.GetSystemInventoryAsync(CancellationToken.None);

        inventory.Success.Should().BeFalse("the read-only allowlist must refuse to follow a traversal link");
        simulator.RequestedPaths.Should().NotContain(
            p => p.Contains("AccountService", StringComparison.Ordinal),
            "the off-allowlist resource must never be requested — the boundary is enforced before any I/O");
    }

    private IBmcDiscoveryDriver CreateDriver(ServiceProvider provider, RedfishEndpoint endpoint)
    {
        var registry = provider.GetRequiredService<IBmcDriverRegistry>();
        var query = new DriverDescriptor("HPE", null, DriverConnectionKind.Redfish, "0.0.0-any");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        var options = new BmcConnectionOptions(endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(5), RedfishBmcFixture.CredentialsRef);
        return factory!.Create(options);
    }

    private ServiceProvider BuildProvider(IIpmiCommandRunner ipmiRunner)
    {
        var services = new ServiceCollection();
        var loggerFactory = new XunitLoggerFactory(_output);
        var metrics = new RedfishMetrics();
        var resolver = new EnvBmcCredentialResolver(_fixture.PinnedEnvLookup);

        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(metrics);
        services.AddSingleton<IBmcDriverFactory>(
            new RedfishBmcDriverFactory(resolver, ipmiRunner, metrics, loggerFactory, _fixture.PinnedEnvLookup));
        services.AddCaissonDriverRegistry();

        return services.BuildServiceProvider();
    }
}
