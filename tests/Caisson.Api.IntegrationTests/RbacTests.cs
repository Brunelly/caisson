using System.Net;
using System.Net.Http;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>RBAC matrix (AC4): 401 anonymous, 403 unrecognised role, 200 for each read role.</summary>
[Collection(ApiCollection.Name)]
public sealed class RbacTests
{
    private readonly CaissonApiFactory _factory;

    public RbacTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_request_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync(LatestPath());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Each_read_role_can_read(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Authenticated_without_a_recognised_role_is_forbidden()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private string LatestPath()
        => $"/api/racks/{_factory.Seed.RackId}/topology/snapshots/latest";
}
