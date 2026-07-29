using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #5: health checks are exempt from rate limiting so a load balancer/orchestrator's frequent,
/// unauthenticated probing can never be throttled — the one behavioural slice of the rate limiter this
/// suite can exercise without a real authenticated principal (the per-oid partitioning needs a genuine
/// claim, which only the Postgres-backed <c>CaissonApiFactory</c> suite can supply via TestAuthHandler).
/// </summary>
public sealed class RateLimiterTests
{
    [Fact]
    public async Task Health_live_is_never_rate_limited()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AzureAd:Authority", "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0");
            builder.UseSetting("AzureAd:Audience", "api://caisson-test");
        });
        using var client = factory.CreateClient();

        // Well past the tightest configured window (DiscoveryTrigger: 20/min) and comfortably above the
        // global default (600/min) too, on an endpoint that DisableRateLimiting() — every response must
        // still be 200, never 429.
        for (var i = 0; i < 50; i++)
        {
            var response = await client.GetAsync("/health/live");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
