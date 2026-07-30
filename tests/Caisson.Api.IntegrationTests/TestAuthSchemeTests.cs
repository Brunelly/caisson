using System.Net;
using System.Net.Http.Json;
using Caisson.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Unit + integration coverage for the environment-gated test-auth scheme (ADR 0018): the fail-closed
/// startup guard, the refuse-to-boot behaviour under a real host, and the least-privilege principal.
/// </summary>
public sealed class TestAuthSchemeTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_throws_when_enabled_outside_development_or_testing(string environmentName)
    {
        var environment = new FakeHostEnvironment(environmentName);

        var act = () => TestAuthStartupGuard.Validate(environment, enableTestAuth: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{environmentName}*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_does_not_throw_when_enabled_under_development_or_testing(string environmentName)
    {
        var environment = new FakeHostEnvironment(environmentName);

        var act = () => TestAuthStartupGuard.Validate(environment, enableTestAuth: true);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_never_throws_when_disabled(string environmentName)
    {
        var environment = new FakeHostEnvironment(environmentName);

        var act = () => TestAuthStartupGuard.Validate(environment, enableTestAuth: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Host_refuses_to_boot_when_test_auth_is_enabled_outside_development_or_testing()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Testing:EnableTestAuth", "true");
        });

        // WebApplicationFactory builds the host lazily; forcing that here proves the host construction
        // itself throws (refuses to boot) — not merely that a later request would be rejected.
        var act = () => factory.CreateClient();

        var thrown = act.Should().Throw<Exception>().Which;
        var messages = thrown.Message + (thrown.InnerException?.Message ?? string.Empty);
        messages.Should().Contain("Testing:EnableTestAuth");
    }

    [SkippableFact]
    public async Task Least_privilege_principal_can_read_topology_but_cannot_trigger_discovery()
    {
        var factory = new TestAuthEnabledApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        try
        {
            Skip.IfNot(factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

            var client = factory.CreateClient();

            var read = await client.GetAsync($"/api/racks/{factory.Seed.RackId}/topology/snapshots/latest");
            var readBody = await read.Content.ReadAsStringAsync();
            read.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the fixed principal holds ReadOnly, which satisfies TopologyRead (response body: {0})",
                readBody);

            var trigger = await client.PostAsJsonAsync(
                $"/api/racks/{factory.Seed.RackId}/discovery-jobs", new { mode = "OnDemand" });
            trigger.StatusCode.Should().Be(
                HttpStatusCode.Forbidden, "the fixed principal holds only ReadOnly, never Operator/Admin");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Caisson.Api.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// A dedicated, isolated-Postgres factory with the real test-auth scheme active (Environment=Testing,
    /// Testing:EnableTestAuth=true) — distinct from <see cref="CaissonApiFactory"/>'s header-driven
    /// <see cref="TestAuthHandler"/>, which is a different, in-process-only RBAC seam.
    /// </summary>
    private sealed class TestAuthEnabledApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly PostgresHarness _harness = new();

        public bool Available => _harness.Available;

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
            builder.UseEnvironment("Testing");
            builder.UseSetting("Testing:EnableTestAuth", "true");
            builder.UseSetting("ConnectionStrings:Caisson", _harness.ConnectionString);
        }

        async Task IAsyncLifetime.DisposeAsync()
        {
            await _harness.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
