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

    [Fact]
    public async Task Latest_returns_the_newest_snapshot_with_graph()
    {
        if (!_factory.Available)
        {
            return;
        }

        using var doc = await GetJson($"{Base}/snapshots/latest");
        doc.RootElement.GetProperty("snapshot").GetProperty("version").GetInt32()
            .Should().Be(_factory.Seed.SecondVersion);
        doc.RootElement.GetProperty("graph").GetProperty("servers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task History_is_ordered_and_keyset_paginated()
    {
        if (!_factory.Available)
        {
            return;
        }

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

    [Fact]
    public async Task Detail_returns_the_requested_snapshot()
    {
        if (!_factory.Available)
        {
            return;
        }

        using var doc = await GetJson($"{Base}/snapshots/{_factory.Seed.FirstSnapshotId}");
        doc.RootElement.GetProperty("snapshot").GetProperty("version").GetInt32()
            .Should().Be(_factory.Seed.FirstVersion);
    }

    [Fact]
    public async Task Graph_surfaces_unmapped_ports()
    {
        if (!_factory.Available)
        {
            return;
        }

        using var doc = await GetJson($"{Base}/snapshots/latest/graph");
        var unmapped = doc.RootElement.GetProperty("unmappedPorts").EnumerateArray()
            .Select(p => p.GetProperty("portName").GetString());
        unmapped.Should().Contain("ether3");
    }

    [Fact]
    public async Task Diff_reports_the_modified_server()
    {
        if (!_factory.Available)
        {
            return;
        }

        var url = $"{Base}/diff?from={_factory.Seed.FirstSnapshotId}&to={_factory.Seed.SecondSnapshotId}";
        using var doc = await GetJson(url);

        var diffs = doc.RootElement.GetProperty("diffs").EnumerateArray().ToList();
        diffs.Should().Contain(d =>
            d.GetProperty("entityType").GetString() == "Server"
            && d.GetProperty("entityStableKey").GetString() == "uuid-1"
            && d.GetProperty("changeType").GetString() == "Modified");
    }

    [Fact]
    public async Task Entity_detail_returns_latest_and_history()
    {
        if (!_factory.Available)
        {
            return;
        }

        using var doc = await GetJson($"{Base}/entities/Server/uuid-1");
        doc.RootElement.GetProperty("latest").ValueKind.Should().NotBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("history").GetArrayLength().Should().BeGreaterThan(0);

        using var history = await GetJson($"{Base}/entities/Server/uuid-1/history");
        history.RootElement.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Audit_trail_returns_discovery_events()
    {
        if (!_factory.Available)
        {
            return;
        }

        var url = $"/api/racks/{_factory.Seed.RackId}/audit?from=2026-07-01T00:00:00Z&to=2027-01-01T00:00:00Z";
        using var doc = await GetJson(url);

        var actions = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString());
        actions.Should().Contain("discovery.persisted");
    }

    [Theory]
    [InlineData("/snapshots?pageSize=0")]
    [InlineData("/snapshots?pageSize=9999")]
    [InlineData("/snapshots?cursor=%40%40notacursor")]
    public async Task Invalid_pagination_returns_problem_details(string suffix)
    {
        if (!_factory.Available)
        {
            return;
        }

        var response = await ReadClient().GetAsync($"{Base}{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblemDetailsAsync(response, expectedStatus: 400);
    }

    [Fact]
    public async Task Missing_snapshot_returns_not_found_problem_details()
    {
        if (!_factory.Available)
        {
            return;
        }

        var response = await ReadClient().GetAsync($"{Base}/snapshots/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertProblemDetailsAsync(response, expectedStatus: 404);
    }

    [Fact]
    public async Task Missing_entity_returns_not_found()
    {
        if (!_factory.Available)
        {
            return;
        }

        var response = await ReadClient().GetAsync($"{Base}/entities/Server/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_entity_type_returns_bad_request()
    {
        if (!_factory.Available)
        {
            return;
        }

        var response = await ReadClient().GetAsync($"{Base}/entities/NotAType/whatever");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_rack_returns_not_found()
    {
        if (!_factory.Available)
        {
            return;
        }

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
