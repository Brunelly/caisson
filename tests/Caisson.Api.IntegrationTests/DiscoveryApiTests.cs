using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// RBAC + behaviour matrix for the discovery orchestration endpoints (story #8, AC2/AC4, NFR3/NFR5).
/// DB-backed and gated on <see cref="CaissonApiFactory.Available"/> via <c>SkippableFact</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscoveryApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CaissonApiFactory _factory;

    public DiscoveryApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_trigger_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/racks/{_factory.Seed.Discovery.RackId}/discovery-jobs", new { mode = "OnDemand" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task ReadOnly_cannot_trigger_and_creates_no_job()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        var response = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "ro", "ReadOnly",
            new { mode = "OnDemand" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // No job row was created for the rack.
        var list = await Send(HttpMethod.Get, $"/api/racks/{rackId}/discovery-jobs", "admin", "Admin");
        var page = await ReadJson(list);
        page.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [SkippableTheory]
    [InlineData("Operator")]
    [InlineData("Admin")]
    public async Task Operator_and_admin_can_trigger(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        var response = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "u", role,
            new { mode = "OnDemand", dryRun = true });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        var body = await ReadJson(response);
        body.GetProperty("jobId").GetGuid().Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task Client_supplied_scheduled_mode_is_rejected()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        var response = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "Scheduled" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Over_length_idempotency_key_is_rejected_with_400_not_500()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        // 201 chars: exceeds the varchar(200) column bound. Must be a validation 400, not a 22001-driven 500.
        var response = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "OnDemand", idempotencyKey = new string('k', 201) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Every_role_can_read_status_list_and_detail(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var discovery = _factory.Seed.Discovery;

        var status = await Send(HttpMethod.Get, $"/api/racks/{discovery.RackId}/discovery-status", "u", role);
        status.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Send(HttpMethod.Get, $"/api/racks/{discovery.RackId}/discovery-jobs", "u", role);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Send(HttpMethod.Get, $"/api/discovery-jobs/{discovery.CompletedJobId}", "u", role);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);

        // AC4: no secret material is ever surfaced.
        foreach (var response in new[] { status, list, detail })
        {
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain("credentialsRef");
            text.Should().NotContain("kv://");
        }
    }

    [SkippableFact]
    public async Task Schedule_put_is_admin_only()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();
        var body = new { enabled = true, intervalSeconds = 900, jitterSeconds = 60 };

        var operatorPut = await Send(HttpMethod.Put, $"/api/racks/{rackId}/discovery-schedule", "op", "Operator", body);
        operatorPut.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adminPut = await Send(HttpMethod.Put, $"/api/racks/{rackId}/discovery-schedule", "admin", "Admin", body);
        adminPut.StatusCode.Should().Be(HttpStatusCode.OK);

        var read = await Send(HttpMethod.Get, $"/api/racks/{rackId}/discovery-schedule", "ro", "ReadOnly");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(read)).GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [SkippableFact]
    public async Task Concurrent_triggers_for_one_rack_yield_one_202_and_one_409()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        var t1 = Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" });
        var t2 = Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" });
        var responses = await Task.WhenAll(t1, t2);

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }

    [SkippableFact]
    public async Task Repeated_idempotency_key_returns_the_same_job()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();
        var body = new { mode = "OnDemand", idempotencyKey = "client-key-1" };

        var first = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", body);
        var second = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", body);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstId = (await ReadJson(first)).GetProperty("jobId").GetGuid();
        var secondId = (await ReadJson(second)).GetProperty("jobId").GetGuid();
        secondId.Should().Be(firstId);
    }

    [SkippableFact]
    public async Task Triggered_job_runs_to_terminal_in_the_background_and_persists_a_snapshot()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync();

        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        JsonElement detail = default;
        var terminal = false;
        for (var i = 0; i < 60 && !terminal; i++)
        {
            await Task.Delay(500);
            var response = await Send(HttpMethod.Get, $"/api/discovery-jobs/{jobId}", "op", "Operator");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            detail = await ReadJson(response);
            var status = detail.GetProperty("status").GetString();
            terminal = status is "Succeeded" or "Failed" or "Canceled";
        }

        terminal.Should().BeTrue("the background runner must drive the job to a terminal state, not the HTTP request");
        detail.GetProperty("status").GetString().Should().Be("Succeeded");
        detail.GetProperty("resultSnapshotId").GetString().Should().NotBeNullOrEmpty();
    }

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? user, string? roles, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text, Json);
    }
}
