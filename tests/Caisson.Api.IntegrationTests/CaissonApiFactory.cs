using Caisson.Infrastructure.Persistence;
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
/// The whole DB-backed suite gates on <see cref="Available"/>, staying green when Docker/Postgres is
/// absent.
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
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }
}
