using System.Net.Http;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// The Angular SPA's CORS policy (story #10, ADR 0015): origins are config-driven and methods are
/// restricted to GET (topology/audit query endpoints) and POST (required for the SignalR hub's
/// negotiate handshake) rather than <c>AllowAnyMethod()</c> — a defense-in-depth tightening of the
/// preflight contract to what the SPA actually calls cross-origin.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorsPolicyTests
{
    private const string AllowedOrigin = "http://localhost:4200";

    private readonly CaissonApiFactory _factory;

    public CorsPolicyTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Preflight_allows_GET_for_a_query_endpoint_from_the_configured_origin()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var client = ClientWithAllowedOrigin();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/racks/00000000-0000-0000-0000-000000000000/topology/snapshots/latest");
        preflight.Headers.Add("Origin", AllowedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(preflight);

        response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain(v => v.Contains("GET"));
    }

    [SkippableFact]
    public async Task Preflight_allows_POST_required_for_the_SignalR_hub_negotiate_handshake()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var client = ClientWithAllowedOrigin();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/hubs/topology/negotiate");
        preflight.Headers.Add("Origin", AllowedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(preflight);

        response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain(v => v.Contains("POST"));
        response.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle("true");
    }

    [SkippableFact]
    public async Task Preflight_rejects_a_method_the_read_only_API_never_serves_cross_origin()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var client = ClientWithAllowedOrigin();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/racks/00000000-0000-0000-0000-000000000000/topology/snapshots/latest");
        preflight.Headers.Add("Origin", AllowedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");

        var response = await client.SendAsync(preflight);

        // The CORS middleware still answers the preflight, but DELETE must never appear among the
        // methods it grants — that's what stops the browser from ever sending the real DELETE request.
        if (response.Headers.TryGetValues("Access-Control-Allow-Methods", out var allowed))
        {
            allowed.Should().NotContain(v => v.Contains("DELETE"));
        }
    }

    private HttpClient ClientWithAllowedOrigin() => _factory
        .WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin))
        .CreateClient();
}
