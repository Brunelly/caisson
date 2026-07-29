using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caisson.Domain.Topology;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Simulators;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Caisson.VirtualRack.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Hosts <c>Caisson.Api</c> in-process against an isolated Postgres/Redis (mirroring
/// <c>CaissonApiFactory</c>), but — unlike that factory — drives the REAL
/// <c>RouterOsSwitchDriverFactory</c>/<c>RedfishBmcDriverFactory</c> that <c>AddCaissonOrchestration</c>
/// already registers by default. Only <see cref="IRackDefinitionProvider"/> is overridden (to point at the
/// live in-process simulators instead of config); credentials are supplied the same way production does —
/// via the <c>CAISSON_SWITCH_*</c>/<c>CAISSON_BMC_*</c> environment variables the real driver factories
/// read. Three simulator endpoints are kept running so a test can select a failure scenario per rack
/// (<see cref="RackScenario"/>) without restarting the host: the happy-path switch/BMC pair, an
/// auth-failing BMC, and a closed loopback port that models an unreachable switch.
/// </summary>
public sealed class VirtualRackApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string SwitchUsername = "vrack-switch";
    private const string SwitchPassword = "sim-only-password";
    private const string BmcUsername = "vrack-bmc";
    private const string BmcPassword = "sim-only-password";

    private readonly PostgresHarness _harness = new();
    private readonly RedisHarness _redis = new();
    private readonly ConcurrentDictionary<Guid, RackScenario> _scenarios = new();

    private X509Certificate2? _bmcCertificate;
    private RouterOsApiSimulator? _switchSimulator;
    private RedfishSimulator? _bmcSimulator;
    private RedfishSimulator? _bmcAuthFailSimulator;
    private TcpListener? _unreachableSwitchListener;

    /// <summary>Whether an ephemeral Postgres was provisioned; when false the suite skips its cases.</summary>
    public bool Available => _harness.Available;

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        await _redis.InitializeAsync();
        if (!_harness.Available)
        {
            return;
        }

        Environment.SetEnvironmentVariable("CAISSON_SWITCH_USERNAME", SwitchUsername);
        Environment.SetEnvironmentVariable("CAISSON_SWITCH_PASSWORD", SwitchPassword);
        Environment.SetEnvironmentVariable("CAISSON_BMC_USERNAME", BmcUsername);
        Environment.SetEnvironmentVariable("CAISSON_BMC_PASSWORD", BmcPassword);

        _bmcCertificate = GenerateCertificate();
        Environment.SetEnvironmentVariable(
            "CAISSON_BMC_TLS_FINGERPRINT", Convert.ToHexString(SHA256.HashData(_bmcCertificate.GetRawCertData())));

        _switchSimulator = new RouterOsApiSimulator(RouterOsProfileRenderer.Render(), SwitchUsername, SwitchPassword);
        _switchSimulator.Start();

        _bmcSimulator = new RedfishSimulator(RedfishProfileRenderer.Render(VirtualRackDefinition.ServerId), _bmcCertificate);
        _bmcSimulator.Start();

        _bmcAuthFailSimulator = new RedfishSimulator(RedfishProfileRenderer.RenderAuthFailure(), _bmcCertificate);
        _bmcAuthFailSimulator.Start();

        // A closed loopback port: bound then immediately released, so a connection attempt is refused
        // (ECONNREFUSED) rather than timing out — a deterministic "unreachable switch" (AC3).
        _unreachableSwitchListener = new TcpListener(IPAddress.Loopback, 0);
        _unreachableSwitchListener.Start();
        UnreachableSwitchPort = ((IPEndPoint)_unreachableSwitchListener.LocalEndpoint).Port;
        _unreachableSwitchListener.Stop();
    }

    private int UnreachableSwitchPort { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Caisson", _harness.ConnectionString);
        if (_redis.Available)
        {
            builder.UseSetting("ConnectionStrings:Redis", _redis.ConnectionString);
            builder.UseSetting("Realtime:Enabled", "true");
            builder.UseSetting("Realtime:HeartbeatSeconds", "1");
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<CaissonDbContext>));
            services.RemoveAll(typeof(DbContextOptions));
            services.AddDbContext<CaissonDbContext>(options => options.UseNpgsql(_harness.ConnectionString));

            // Header-driven RBAC for the WebApplicationFactory's in-process HTTP calls — distinct from the
            // production environment-gated test-auth scheme added in Story #11 Step 4.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // The ONLY override: AddCaissonOrchestration already registered the REAL
            // RouterOsSwitchDriverFactory/RedfishBmcDriverFactory; only the rack definition needs to point
            // at the live simulator endpoints instead of Discovery:Racks config.
            services.RemoveAll(typeof(IRackDefinitionProvider));
            services.AddScoped<IRackDefinitionProvider>(_ => new VirtualRackDefinitionProvider(this));

            services.Configure<DiscoveryOrchestrationOptions>(options =>
            {
                options.SchedulerEnabled = false;
                options.RunnerEnabled = true;
                options.RunnerPollSeconds = 1;
                options.RetryBaseDelayMs = 0;
                options.HeartbeatStalenessSeconds = 5;
            });
        });
    }

    /// <summary>Creates a fresh rack bound to <paramref name="scenario"/> and returns its id.</summary>
    public async Task<Guid> CreateRackAsync(string? name = null, RackScenario scenario = RackScenario.Happy)
    {
        var rackId = Guid.NewGuid();
        _scenarios[rackId] = scenario;

        await using var context = _harness.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), name ?? "Virtual Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private RackDefinition BuildDefinition(Guid rackId)
    {
        var scenario = _scenarios.GetValueOrDefault(rackId, RackScenario.Happy);

        var switchPort = scenario == RackScenario.SwitchUnreachable ? UnreachableSwitchPort : _switchSimulator!.Port;
        // The virtual-rack switch simulator speaks plaintext RouterOS API, so the explicit AllowPlaintext
        // opt-in is required now that TLS is the fail-closed default (finding #8).
        var switchDevice = new DeviceDefinition(
            VirtualRackDefinition.SwitchId, "MikroTik", null, DriverConnectionKind.RouterOsApi,
            _switchSimulator!.Host, switchPort, TimeSpan.FromSeconds(5), "sw1_creds",
            UseTls: false, AllowPlaintext: true);

        var bmcSimulator = scenario == RackScenario.BmcAuthFailure ? _bmcAuthFailSimulator! : _bmcSimulator!;
        var serverDevice = new DeviceDefinition(
            VirtualRackDefinition.ServerId, "HPE", null, DriverConnectionKind.Redfish,
            bmcSimulator.Host, bmcSimulator.Port, TimeSpan.FromSeconds(5), "srv1_creds");

        return new RackDefinition(rackId, "vrack-" + rackId.ToString("N"), new[] { switchDevice }, new[] { serverDevice });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_switchSimulator is not null)
        {
            await _switchSimulator.DisposeAsync();
        }

        if (_bmcSimulator is not null)
        {
            await _bmcSimulator.DisposeAsync();
        }

        if (_bmcAuthFailSimulator is not null)
        {
            await _bmcAuthFailSimulator.DisposeAsync();
        }

        _bmcCertificate?.Dispose();

        await _redis.DisposeAsync();
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    private static X509Certificate2 GenerateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=vrack.sim.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning disable SYSLIB0057 // net8 has no X509CertificateLoader; the constructor is the supported path here.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
    }

    /// <summary>The failure scenario a test-created rack should exercise (default: the happy path).</summary>
    public enum RackScenario
    {
        /// <summary>The switch and BMC both resolve to the live, well-behaved simulators.</summary>
        Happy,

        /// <summary>The BMC resolves to a simulator that answers 401 to every request (AC3).</summary>
        BmcAuthFailure,

        /// <summary>The switch resolves to a closed loopback port (connection refused, AC3).</summary>
        SwitchUnreachable,
    }

    /// <summary>
    /// Resolves any rack to the fixed <see cref="VirtualRackDefinition"/> devices, pointed at whichever
    /// live simulator endpoint the rack's registered <see cref="RackScenario"/> selects.
    /// </summary>
    private sealed class VirtualRackDefinitionProvider : IRackDefinitionProvider
    {
        private readonly VirtualRackApiFactory _factory;

        public VirtualRackDefinitionProvider(VirtualRackApiFactory factory) => _factory = factory;

        public Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
            => Task.FromResult(_factory.BuildDefinition(rackId));
    }
}
