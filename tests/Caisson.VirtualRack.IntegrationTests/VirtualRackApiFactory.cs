using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caisson.Domain.Topology;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The distinct vendor descriptor a <see cref="RackScenario.WithheldRollback"/> rack's switch device
    /// declares, so <c>ISwitchMutatingDriverRegistry</c>/<c>ISwitchDriverRegistry</c> route it to the
    /// additively-registered scripted factories instead of the real MikroTik ones (Task #115).
    /// </summary>
    public const string MockWithheldVendor = "MockWithheld";

    private readonly PostgresHarness _harness = new();
    private readonly RedisHarness _redis = new();
    private readonly ConcurrentDictionary<Guid, RackScenario> _scenarios = new();

    // Task #115: the scripted withheld-confirmation driver factory for RackScenario.WithheldRollback,
    // registered additively in ConfigureWebHost below. Kept as a field (not a local in the lambda) so
    // tests can read WithheldDriverCallCount after the host is built.
    private readonly ScriptedWithheldMutatingDriverFactory _withheldMutatingDriverFactory;

    private X509Certificate2? _bmcCertificate;
    private RouterOsApiSimulator? _switchSimulator;
    private RouterOsApiSimulator? _writeCapableSwitchSimulator;
    private RedfishSimulator? _bmcSimulator;
    private RedfishSimulator? _bmcAuthFailSimulator;
    private TcpListener? _unreachableSwitchListener;

    public VirtualRackApiFactory()
        => _withheldMutatingDriverFactory = new ScriptedWithheldMutatingDriverFactory(() => _writeCapableSwitchSimulator!);

    /// <summary>Whether an ephemeral Postgres was provisioned; when false the suite skips its cases.</summary>
    public bool Available => _harness.Available;

    /// <summary>How many device connections the scripted withheld-confirmation driver created (Task #115).</summary>
    public int WithheldDriverCallCount => _withheldMutatingDriverFactory.CallCount;

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

        // A SEPARATE simulator instance (not a mutation of _switchSimulator) seeded with
        // RenderStateful()'s SimulatorSwitchState — only racks registered under RackScenario.
        // DriftApplyCapable (or WithheldRollback) are pointed at it, so every existing detection-only test
        // driving the stateless _switchSimulator is byte-for-byte unaffected (Task #112).
        _writeCapableSwitchSimulator = new RouterOsApiSimulator(RouterOsProfileRenderer.RenderStateful(), SwitchUsername, SwitchPassword);
        _writeCapableSwitchSimulator.Start();

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

            // Story #64: the periodic scheduler/pruner ticks are not needed for determinism in these
            // tests — DriftRecomputeRunner (always on, no enable flag) already reacts immediately to the
            // real event hooks fired by snapshot/desired-state ingestion, so disabling the sweeps here
            // avoids a second, timing-dependent recompute racing the one under test (mirrors the
            // DiscoveryOrchestrationOptions determinism override immediately above).
            services.Configure<DriftOrchestrationOptions>(options =>
            {
                options.SchedulerEnabled = false;
                options.RetentionEnabled = false;
            });

            // Task #112: fast, deterministic drift-apply job polling — mirrors the
            // DiscoveryOrchestrationOptions override above. AddCaissonDriftApply is already called
            // unconditionally by the real Program (the only new registrations tests need are the
            // additive scripted driver factories some test classes add via ConfigureTestServices).
            services.Configure<DriftApplyOrchestrationOptions>(options =>
            {
                options.RunnerEnabled = true;
                options.RunnerPollSeconds = 1;
                options.RetryBaseDelayMs = 0;
                options.HeartbeatStalenessSeconds = 5;
            });

            // Task #115: additive registrations for RackScenario.WithheldRollback's ONE rack — the real
            // RouterOsSwitchMutatingDriverFactory/RouterOsSwitchDriverFactory stay registered (via
            // AddCaissonOrchestration/AddCaissonDriftApply above) for every other rack's "MikroTik" vendor;
            // these answer only the distinct MockWithheldVendor. See ScriptedWithheldMutatingDriver.cs.
            services.AddSingleton<ISwitchMutatingDriverFactory>(_withheldMutatingDriverFactory);
            services.AddSingleton<ISwitchDriverFactory>(provider => new MockWithheldReadDriverFactory(
                provider.GetRequiredService<ISwitchCredentialResolver>(),
                provider.GetRequiredService<RouterOsMetrics>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<IHostEnvironment>()));
        });
    }

    /// <summary>
    /// Creates a fresh rack bound to <paramref name="scenario"/> and returns its id.
    /// <paramref name="externalKey"/> defaults to a random <c>"rack-{guid}"</c> slug; pass an explicit
    /// value (e.g. <c>DesiredStateYamlRenderer.RackSlug</c>) when a test also ingests desired state for
    /// this rack — drift joins desired↔observed via <c>DesiredStateVersion.RackSlug == Rack.ExternalKey</c>
    /// (ADR 0029), so the two must be seeded with the SAME slug for that rack's drift to compute at all.
    /// </summary>
    public async Task<Guid> CreateRackAsync(string? name = null, RackScenario scenario = RackScenario.Happy, string? externalKey = null)
    {
        var rackId = Guid.NewGuid();
        _scenarios[rackId] = scenario;

        await using var context = _harness.CreateContext();
        context.Racks.Add(new Rack(rackId, externalKey ?? "rack-" + rackId.ToString("N"), name ?? "Virtual Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private RackDefinition BuildDefinition(Guid rackId)
    {
        var scenario = _scenarios.GetValueOrDefault(rackId, RackScenario.Happy);

        // DriftApplyCapable (and WithheldRollback) racks are pointed at the SEPARATE stateful simulator
        // instance seeded by RouterOsProfileRenderer.RenderStateful() — every other scenario keeps using
        // the original stateless _switchSimulator, unaffected (Task #112).
        var writeCapable = scenario is RackScenario.DriftApplyCapable or RackScenario.WithheldRollback;
        var activeSimulator = writeCapable ? _writeCapableSwitchSimulator! : _switchSimulator!;

        var switchPort = scenario == RackScenario.SwitchUnreachable ? UnreachableSwitchPort : activeSimulator.Port;
        // The virtual-rack switch simulator speaks plaintext RouterOS API, so the explicit AllowPlaintext
        // opt-in is required now that TLS is the fail-closed default (finding #8).
        //
        // WithheldRollback (Task #115) deliberately declares a DISTINCT Vendor/ConnectionKind
        // ("MockWithheld"/Ssh) — still pointed at the same real, in-process stateful simulator host/port —
        // so ISwitchMutatingDriverRegistry.TryResolve routes drift-apply's device write to the scripted
        // withheld-confirmation driver (registered additively for that vendor) instead of the real
        // RouterOsSwitchMutatingDriverFactory, while a matching "MockWithheld" read-side factory (also
        // registered additively) keeps discovery talking to the REAL simulator over the real protocol —
        // see DriftApplyRollbackEndToEndTests / ADR 0035.
        var switchVendor = scenario == RackScenario.WithheldRollback ? MockWithheldVendor : "MikroTik";
        var switchConnectionKind = scenario == RackScenario.WithheldRollback
            ? DriverConnectionKind.Ssh
            : DriverConnectionKind.RouterOsApi;
        var switchDevice = new DeviceDefinition(
            VirtualRackDefinition.SwitchId, switchVendor, null, switchConnectionKind,
            activeSimulator.Host, switchPort, TimeSpan.FromSeconds(5), "sw1_creds",
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

        if (_writeCapableSwitchSimulator is not null)
        {
            await _writeCapableSwitchSimulator.DisposeAsync();
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

        /// <summary>
        /// The switch resolves to the stateful, write-capable simulator (RenderStateful) via the real
        /// MikroTik vendor/connection-kind — drift-apply's device write mutates real simulator state
        /// (Task #112/#114).
        /// </summary>
        DriftApplyCapable,

        /// <summary>
        /// The switch resolves to the SAME stateful simulator as <see cref="DriftApplyCapable"/>, but under
        /// the distinct <see cref="MockWithheldVendor"/> descriptor, so drift-apply's device write routes to
        /// a scripted withheld-confirmation driver double instead of the real one (Task #115).
        /// </summary>
        WithheldRollback,
    }

    /// <summary>Reads the write-capable simulator's current access VLAN (PVID) for <paramref name="port"/>, or null if unknown.</summary>
    public int? GetSwitchPortAccessVlan(string port) => _writeCapableSwitchSimulator!.GetPortAccessVlan(port);

    /// <summary>
    /// Forces the write-capable simulator's port PVID to a known baseline. The write-capable simulator is
    /// a SINGLE shared instance across every rack in this xUnit collection (tests within a collection run
    /// sequentially but in an unspecified order) — a device-mutating test must call this before seeding, so
    /// its "before" VLAN assumption holds regardless of what an earlier test in the same run left behind.
    /// </summary>
    public void ResetSwitchPortAccessVlanForTest(string port, int pvid) => _writeCapableSwitchSimulator!.SetPortAccessVlanForTest(port, pvid);

    /// <summary>Advances the write-capable simulator's virtual clock (no real sleep) so an armed confirmed-commit rollback can become due.</summary>
    public void AdvanceSwitchTime(TimeSpan delta) => _writeCapableSwitchSimulator!.AdvanceTime(delta);

    /// <summary>Fires any confirmed-commit rollbacks on the write-capable simulator whose window has elapsed.</summary>
    public void FireDueSwitchRollbacks() => _writeCapableSwitchSimulator!.FireDueRollbacks();

    /// <summary>Whether the write-capable simulator has an armed, not-yet-fired rollback for <paramref name="port"/>.</summary>
    public bool HasPendingRollback(string port) => _writeCapableSwitchSimulator!.HasPendingRollback(port);

    /// <summary>Every command path the write-capable simulator has received, in order (device-call-count assertions).</summary>
    public IReadOnlyList<string> ReceivedSwitchCommands => _writeCapableSwitchSimulator!.ReceivedCommands;

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
