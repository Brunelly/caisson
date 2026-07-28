using Caisson.Domain.Topology;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Hosts <c>Caisson.Api</c> in-process against an isolated Postgres (via <see cref="PostgresHarness"/>),
/// seeds a realistic rack, and swaps JWT/Entra auth for the header-driven <see cref="TestAuthHandler"/>.
/// The whole DB-backed suite gates on <see cref="Available"/> via <c>Assert.SkipUnless</c>, so cases are
/// reported as <b>skipped</b> (not passed) when Docker/Postgres is absent — a visibly distinct signal
/// from a genuine pass.
/// </summary>
public sealed class CaissonApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgresHarness _harness = new();

    /// <summary>Whether an ephemeral Postgres was provisioned; when false the suite skips its cases.</summary>
    public bool Available => _harness.Available;

    /// <summary>The seeded topology identifiers (null when <see cref="Available"/> is false).</summary>
    public SeededTopology Seed { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        if (_harness.Available)
        {
            Seed = await SeedData.SeedAsync(_harness);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Caisson", _harness.ConnectionString);

        builder.ConfigureTestServices(services =>
        {
            // Point the DbContext at the isolated harness database, regardless of ambient env vars.
            services.RemoveAll(typeof(DbContextOptions<CaissonDbContext>));
            services.RemoveAll(typeof(DbContextOptions));
            services.AddDbContext<CaissonDbContext>(options => options.UseNpgsql(_harness.ConnectionString));

            // Replace JWT/Entra auth with the header-driven test scheme (becomes the default scheme).
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Story #8: make discovery deterministic and hardware-free. Any rack resolves a Mock
            // definition, Mock drivers return canned data, the scheduler is off, and retries don't sleep.
            services.RemoveAll(typeof(IRackDefinitionProvider));
            services.AddScoped<IRackDefinitionProvider, TestRackDefinitionProvider>();
            services.AddSingleton<ISwitchDriverFactory, TestSwitchDriverFactory>();
            services.AddSingleton<IBmcDriverFactory, TestBmcDriverFactory>();
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

    /// <summary>Creates a fresh rack in the isolated database and returns its id.</summary>
    public async Task<Guid> CreateRackAsync(string? name = null)
    {
        var rackId = Guid.NewGuid();
        await using var context = _harness.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), name ?? "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }
}
