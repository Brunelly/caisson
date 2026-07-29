using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #18: Swagger/OpenAPI is gated to an explicit allow-list of environments (Development,
/// Testing), not a negative "!IsProduction()" check — a Staging (or any custom) environment must not
/// expose the schema unauthenticated.
/// </summary>
public sealed class SwaggerGatingTests
{
    [Fact]
    public async Task Swagger_json_returns_404_under_staging()
    {
        using var factory = HostUnder("Staging");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        // Swagger's own middleware never runs under Staging, so no endpoint at all matches this request —
        // it falls through to the fallback RequireAuthenticatedUser() policy, which answers unauthenticated
        // requests with 401 before routing would otherwise answer 404. Either way the schema is never
        // served: an anonymous caller gets neither the document nor even a hint that /swagger exists.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Swagger_json_is_served_under_testing()
    {
        using var factory = HostUnder("Testing");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> HostUnder(string environmentName)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            // Satisfy the finding #16/#17 startup guards so the host builds far enough to serve a
            // request — this test is exercising the Swagger gate, not those guards.
            builder.UseSetting("AzureAd:Authority", "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0");
            builder.UseSetting("AzureAd:Audience", "api://caisson-test");
            builder.UseSetting("Authentication:RoleMappings:group-1", "ReadOnly");
        });
}
