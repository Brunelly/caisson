using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Simulators;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.DependencyInjection;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Caisson.VirtualRack.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var connectionString = Environment.GetEnvironmentVariable("CAISSON_DB");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("CAISSON_DB must be set to a PostgreSQL connection string.");
    return 1;
}

const string switchUsername = "vrack-seed-switch";
const string switchPassword = "sim-only-password";
const string bmcUsername = "vrack-seed-bmc";
const string bmcPassword = "sim-only-password";

using var bmcCertificate = GenerateCertificate();
Environment.SetEnvironmentVariable("CAISSON_SWITCH_USERNAME", switchUsername);
Environment.SetEnvironmentVariable("CAISSON_SWITCH_PASSWORD", switchPassword);
Environment.SetEnvironmentVariable("CAISSON_BMC_USERNAME", bmcUsername);
Environment.SetEnvironmentVariable("CAISSON_BMC_PASSWORD", bmcPassword);
Environment.SetEnvironmentVariable(
    "CAISSON_BMC_TLS_FINGERPRINT", Convert.ToHexString(SHA256.HashData(bmcCertificate.GetRawCertData())));

// The real switch/BMC drivers connect to these in-process simulators, rendered from the same ground
// truth ExpectedTopologyBuilder verifies against — "one definition, two renderers" (ADR 0017).
await using var switchSimulator = new RouterOsApiSimulator(RouterOsProfileRenderer.Render(), switchUsername, switchPassword);
switchSimulator.Start();

await using var bmcSimulator = new RedfishSimulator(RedfishProfileRenderer.Render(VirtualRackDefinition.ServerId), bmcCertificate);
bmcSimulator.Start();

var rackDefinitionTemplate = new RackDefinition(
    Guid.Empty,
    "vrack-seed",
    new[]
    {
        new DeviceDefinition(
            VirtualRackDefinition.SwitchId, "MikroTik", null, DriverConnectionKind.RouterOsApi,
            switchSimulator.Host, switchSimulator.Port, TimeSpan.FromSeconds(5), "sw1-creds"),
    },
    new[]
    {
        new DeviceDefinition(
            VirtualRackDefinition.ServerId, "HPE", null, DriverConnectionKind.Redfish,
            bmcSimulator.Host, bmcSimulator.Port, TimeSpan.FromSeconds(5), "srv1-creds"),
    });

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddCaissonPersistence();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCaissonOrchestration(builder.Configuration);

// The only override: AddCaissonOrchestration already registered the real driver factories; only the
// rack definition needs to point at the live simulator endpoints instead of Discovery:Racks config.
builder.Services.RemoveAll(typeof(IRackDefinitionProvider));
builder.Services.AddScoped<IRackDefinitionProvider>(_ => new FixedRackDefinitionProvider(rackDefinitionTemplate));

builder.Services.Configure<DiscoveryOrchestrationOptions>(options =>
{
    options.SchedulerEnabled = false;
    options.RunnerEnabled = true;
    options.RunnerPollSeconds = 1;
    options.RetryBaseDelayMs = 0;
});

using var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
    await db.Database.MigrateAsync();
}

var rackId = Guid.NewGuid();
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
    db.Racks.Add(new Rack(rackId, "vrack-seed-" + rackId.ToString("N"), "Virtual Rack (seeded)", DateTime.UtcNow));
    await db.SaveChangesAsync();
}

// Starts the DiscoveryJobRunner background service, which claims and runs the job enqueued below.
await host.StartAsync();

Guid jobId;
await using (var scope = host.Services.CreateAsyncScope())
{
    var jobs = scope.ServiceProvider.GetRequiredService<IDiscoveryJobService>();
    var result = await jobs.EnqueueAsync(
        rackId, TriggerType.OnDemand, "seeder", ActorType.ServiceAccount, Guid.NewGuid(),
        idempotencyKey: null, dryRun: false, CancellationToken.None);
    jobId = result.JobId;
}

DiscoveryJob? job = null;
var terminal = false;
for (var attempt = 0; attempt < 60 && !terminal; attempt++)
{
    await Task.Delay(TimeSpan.FromMilliseconds(500));
    await using var scope = host.Services.CreateAsyncScope();
    var jobs = scope.ServiceProvider.GetRequiredService<IDiscoveryJobService>();
    job = await jobs.GetJobAsync(jobId, CancellationToken.None);
    terminal = job?.Status is DiscoveryJobStatus.Succeeded or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Canceled;
}

await host.StopAsync();

if (job is null || job.Status != DiscoveryJobStatus.Succeeded)
{
    var status = job?.Status.ToString() ?? "unknown (timed out)";
    Console.Error.WriteLine($"Seeding failed: job {jobId} ended in status {status} (errorCode={job?.ErrorCode}).");
    return 1;
}

// The Angular topology-search index derives a server's label from its hostname (search-index.ts:
// `label: server.hostname ?? server.stableKey`) — a unique, unambiguous single-match term/label.
Console.WriteLine($"E2E_RACK_ID={rackId}");
Console.WriteLine($"E2E_SEARCH_TERM={VirtualRackDefinition.ServerHostName}");
Console.WriteLine($"E2E_SEARCH_LABEL_PART={VirtualRackDefinition.ServerHostName}");
return 0;

static X509Certificate2 GenerateCertificate()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest(
        "CN=vrack-seed.sim.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning disable SYSLIB0057 // net8 has no X509CertificateLoader; the constructor is the supported path here.
    return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
}

/// <summary>Resolves every rack to the same fixed <see cref="RackDefinition"/> template.</summary>
internal sealed class FixedRackDefinitionProvider : IRackDefinitionProvider
{
    private readonly RackDefinition _template;

    public FixedRackDefinitionProvider(RackDefinition template) => _template = template;

    public Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
        => Task.FromResult(_template with { RackId = rackId });
}
