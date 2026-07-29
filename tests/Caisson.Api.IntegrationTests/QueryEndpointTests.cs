using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end read behaviour (AC1/AC3): latest, paginated history, detail, graph, diff, entity
/// detail/history and audit — plus problem-details for invalid pagination and 404s.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QueryEndpointTests
{
    private readonly CaissonApiFactory _factory;

    public QueryEndpointTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Latest_returns_the_newest_snapshot_with_graph()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var doc = await GetJson($"{Base}/snapshots/latest");
        doc.RootElement.GetProperty("snapshot").GetProperty("version").GetInt32()
            .Should().Be(_factory.Seed.SecondVersion);
        doc.RootElement.GetProperty("graph").GetProperty("servers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task History_is_ordered_and_keyset_paginated()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var firstPage = await GetJson($"{Base}/snapshots?pageSize=1");
        firstPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        var firstId = firstPage.RootElement.GetProperty("items")[0].GetProperty("snapshotId").GetString();
        var cursor = firstPage.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        using var secondPage = await GetJson($"{Base}/snapshots?pageSize=1&cursor={Uri.EscapeDataString(cursor!)}");
        secondPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        secondPage.RootElement.GetProperty("items")[0].GetProperty("snapshotId").GetString()
            .Should().NotBe(firstId);

        using var allPages = await GetJson($"{Base}/snapshots");
        allPages.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
        allPages.RootElement.GetProperty("items")[0].GetProperty("version").GetInt32()
            .Should().Be(_factory.Seed.SecondVersion); // newest first
    }

    [SkippableFact]
    public async Task Detail_returns_the_requested_snapshot()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var doc = await GetJson($"{Base}/snapshots/{_factory.Seed.FirstSnapshotId}");
        doc.RootElement.GetProperty("snapshot").GetProperty("version").GetInt32()
            .Should().Be(_factory.Seed.FirstVersion);
    }

    [SkippableFact]
    public async Task Graph_surfaces_unmapped_ports()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        using var doc = await GetJson($"{Base}/snapshots/latest/graph");
        var unmapped = doc.RootElement.GetProperty("unmappedPorts").EnumerateArray()
            .Select(p => p.GetProperty("portName").GetString());
        unmapped.Should().Contain("ether3");
    }

    [SkippableFact]
    public async Task Diff_reports_the_modified_server()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var url = $"{Base}/diff?from={_factory.Seed.FirstSnapshotId}&to={_factory.Seed.SecondSnapshotId}";
        using var doc = await GetJson(url);

        var diffs = doc.RootElement.GetProperty("diffs").EnumerateArray().ToList();
        diffs.Should().Contain(d =>
            d.GetProperty("entityType").GetString() == "Server"
            && d.GetProperty("entityStableKey").GetString() == _factory.Seed.ServerStableKey
            && d.GetProperty("changeType").GetString() == "Modified");
    }

    [SkippableFact]
    public async Task Entity_detail_returns_latest_and_history()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // "srv1|uuid-1" — StableKeys.ForServer prefixes with the device key (finding #3); '|' is
        // percent-encoded (Uri.EscapeDataString does not touch the unreserved 'srv1'/'uuid-1' halves).
        var key = Uri.EscapeDataString(_factory.Seed.ServerStableKey);
        using var doc = await GetJson($"{Base}/entities/Server/{key}");
        doc.RootElement.GetProperty("latest").ValueKind.Should().NotBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("history").GetArrayLength().Should().BeGreaterThan(0);

        // Finding #4: history is now a PagedResult<EntityDiffDto>, not a bare array.
        using var history = await GetJson($"{Base}/entities/Server/history/{key}");
        history.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Entity_with_a_slash_in_its_stable_key_is_reachable()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // The seeded switch port "1/1/1" has stable key "sw1|SW-1|1/1/1" — StableKeys.ForSwitch prefixes
        // with the device key "sw1" (finding #3), then ForSwitchPort appends the port name. The '/'
        // segments must be bound by the catch-all route, not split into extra path segments (which would
        // 404). Both '|' separators are encoded; the slashes are left literal so the catch-all captures them.
        const string key = "sw1%7CSW-1%7C1/1/1";

        using var detail = await GetJson($"{Base}/entities/SwitchPort/{key}");
        detail.RootElement.GetProperty("stableKey").GetString().Should().Be("sw1|SW-1|1/1/1");
        detail.RootElement.GetProperty("history").GetArrayLength().Should().BeGreaterThan(0);

        // Finding #4: history is now a PagedResult<EntityDiffDto>, not a bare array.
        using var history = await GetJson($"{Base}/entities/SwitchPort/history/{key}");
        history.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Audit_trail_returns_discovery_events()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var url = $"/api/racks/{_factory.Seed.RackId}/audit?from=2026-07-01T00:00:00Z&to=2027-01-01T00:00:00Z";
        using var doc = await GetJson(url);

        var actions = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString());
        actions.Should().Contain("discovery.persisted");
    }

    [SkippableTheory]
    [InlineData("/snapshots?pageSize=0")]
    [InlineData("/snapshots?pageSize=9999")]
    [InlineData("/snapshots?cursor=%40%40notacursor")]
    public async Task Invalid_pagination_returns_problem_details(string suffix)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"{Base}{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblemDetailsAsync(response, expectedStatus: 400);
    }

    [SkippableFact]
    public async Task Missing_snapshot_returns_not_found_problem_details()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"{Base}/snapshots/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertProblemDetailsAsync(response, expectedStatus: 404);
    }

    [SkippableFact]
    public async Task Missing_entity_returns_not_found()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"{Base}/entities/Server/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Invalid_entity_type_returns_bad_request()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"{Base}/entities/NotAType/whatever");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Missing_rack_returns_not_found()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await ReadClient().GetAsync($"/api/racks/{Guid.NewGuid()}/topology/snapshots/latest");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private string Base => $"/api/racks/{_factory.Seed.RackId}/topology";

    private HttpClient ReadClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "reader");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "ReadOnly");
        return client;
    }

    private static async Task AssertProblemDetailsAsync(HttpResponseMessage response, int expectedStatus)
    {
        // RFC 7807 problem-details body (the media type may be application/json or problem+json).
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("title", out _).Should().BeTrue();
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(expectedStatus);
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        var response = await ReadClient().GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0} should succeed", url);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
