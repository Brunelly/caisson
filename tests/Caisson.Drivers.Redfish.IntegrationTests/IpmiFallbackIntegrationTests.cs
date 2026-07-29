using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Redfish;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Transport;
using Caisson.Drivers.Simulators;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// AC2 (task #29): the Redfish-first / IPMI-fallback path exercised end-to-end through the driver's
/// <see cref="IIpmiCommandRunner"/> seam with a stubbed runner replaying committed <c>ipmitool</c> text
/// fixtures. When Redfish returns a structurally-insufficient NIC set (empty, or MAC-less), the driver
/// falls back to IPMI and records the data-source provenance. A real-ipmitool run self-skips unless
/// <see cref="RedfishBmcFixture.IpmiHostEnvVar"/> is set.
/// </summary>
public sealed class IpmiFallbackIntegrationTests : IClassFixture<RedfishBmcFixture>
{
    private static readonly IReadOnlyDictionary<string, string> IpmiFixtures = new Dictionary<string, string>
    {
        ["mc info"] = "ipmi-mc-info.txt",
        ["fru print"] = "ipmi-fru-print.txt",
        ["lan print"] = "ipmi-lan-print.txt",
    };

    private readonly RedfishBmcFixture _fixture;
    private readonly ITestOutputHelper _output;

    public IpmiFallbackIntegrationTests(RedfishBmcFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("ilo-empty-nics")]
    [InlineData("ilo-nic-missing-mac")]
    public async Task Insufficient_redfish_nics_fall_back_to_the_ipmi_lan_mac(string profile)
    {
        if (_fixture.UsingRealHardware)
        {
            return; // Simulator-only structural profiles.
        }

        var runner = new FixtureIpmiCommandRunner(IpmiFixtures);
        using var provider = BuildProvider(runner);
        var driver = CreateDriver(provider, _fixture.ResolveEndpoint(profile));

        var nics = await driver.GetNetworkInterfacesAsync(CancellationToken.None);

        nics.Success.Should().BeTrue();
        nics.Value!.Should().Contain(n => n.Mac != null && n.Mac.Value.Value == "001a2b3c4d99");
        nics.Diagnostics.Should().Contain(d => d.ReasonCode == ReasonCode.FallbackSource);
        runner.Invocations.Should().Contain("lan print");
    }

    [Fact]
    public async Task Real_ipmitool_opt_in_reads_the_bmc_when_configured()
    {
        var host = Environment.GetEnvironmentVariable(RedfishBmcFixture.IpmiHostEnvVar);
        if (string.IsNullOrWhiteSpace(host))
        {
            return; // Opt-in only: no real BMC configured.
        }

        var runner = new ProcessIpmiCommandRunner(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessIpmiCommandRunner>.Instance);
        var settings = new IpmiConnectionSettings(
            host, IpmiConnectionSettings.DefaultPort,
            Environment.GetEnvironmentVariable("CAISSON_BMC_USERNAME") ?? "admin",
            Environment.GetEnvironmentVariable("CAISSON_BMC_PASSWORD") ?? string.Empty,
            TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(IpmiReadCommands.LanPrint, settings, CancellationToken.None);

        result.Available.Should().BeTrue("ipmitool must be installed for the real-hardware opt-in");
        _output.WriteLine($"ipmitool lan print exit={result.ExitCode}");
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
            new RedfishBmcDriverFactory(resolver, ipmiRunner, metrics, loggerFactory, new TestHostEnvironment(), _fixture.PinnedEnvLookup));
        services.AddCaissonDriverRegistry();

        return services.BuildServiceProvider();
    }
}
