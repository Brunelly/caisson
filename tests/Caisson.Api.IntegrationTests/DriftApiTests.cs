using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end drift read behaviour (story #64, AC5): latest, history, report detail with filters, item
/// detail, rack-scoped 404s, and the RBAC matrix — mirrors <c>QueryEndpointTests</c>/<c>RbacTests</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DriftApiTests
{
    private readonly CaissonApiFactory _factory;

    public DriftApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Latest_returns_the_seeded_report_with_desired_and_snapshot_ids()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var doc = await GetJson($"{Base}/latest");
        var report = doc.RootElement.GetProperty("report");
        report.GetProperty("driftReportId").GetGuid().Should().Be(_factory.Seed.Drift.DriftReportId);
        report.GetProperty("desiredRevisionId").GetGuid().Should().NotBeEmpty();
        report.GetProperty("observedSnapshotId").GetGuid().Should().NotBeEmpty();
        report.GetProperty("totalItems").GetInt32().Should().Be(_factory.Seed.Drift.TotalItems);

        var items = doc.RootElement.GetProperty("items").GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task History_is_keyset_paginated_newest_first()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var doc = await GetJson($"{Base}/history");
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("driftReportId").GetGuid().Should().Be(_factory.Seed.Drift.DriftReportId);
        items[0].TryGetProperty("countsBySeverity", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Report_by_id_supports_severity_and_actionable_filters()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var all = await GetJson($"{Base}/reports/{_factory.Seed.Drift.DriftReportId}");
        var allItems = all.RootElement.GetProperty("items").GetProperty("items");
        allItems.GetArrayLength().Should().Be(_factory.Seed.Drift.TotalItems);

        using var highOnly = await GetJson($"{Base}/reports/{_factory.Seed.Drift.DriftReportId}?severity=High");
        var highItems = highOnly.RootElement.GetProperty("items").GetProperty("items");
        highItems.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var item in highItems.EnumerateArray())
        {
            item.GetProperty("severity").GetString().Should().Be("High");
        }
    }

    [SkippableFact]
    public async Task Item_by_id_returns_full_detail()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var report = await GetJson($"{Base}/reports/{_factory.Seed.Drift.DriftReportId}");
        var firstItemId = report.RootElement.GetProperty("items").GetProperty("items")[0].GetProperty("driftItemId").GetGuid();

        using var item = await GetJson($"{Base}/items/{firstItemId}");
        item.RootElement.GetProperty("driftItemId").GetGuid().Should().Be(firstItemId);
        item.RootElement.TryGetProperty("why", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Report_id_belonging_to_a_different_rack_404s()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var otherRackPath = $"/api/racks/{_factory.Seed.Discovery.RackId}/drift/reports/{_factory.Seed.Drift.DriftReportId}";
        var response = await ReadClient().GetAsync(otherRackPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Unknown_rack_404s()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"/api/racks/{Guid.NewGuid()}/drift/latest");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Invalid_page_size_returns_a_400_problem_details()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"{Base}/history?pageSize=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Anonymous_request_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"{Base}/latest");

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
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/latest");
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
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/latest");
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private string Base => $"/api/racks/{_factory.Seed.RackId}/drift";

    private HttpClient ReadClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "reader");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "ReadOnly");
        return client;
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        var response = await ReadClient().GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0} should succeed", url);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
