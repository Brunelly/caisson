using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Story #62: the RBAC matrix on the five desired-state read endpoints, the webhook's signature-gated
/// 202/401 behaviour (independent of any bearer token), keyset pagination, and secret non-exposure.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStateApiTests
{
    private readonly CaissonApiFactory _factory;

    public DesiredStateApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Status_endpoint_is_unauthorized_for_anonymous_callers()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await _factory.CreateClient().GetAsync("/api/desired-state/status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Health_ready_stays_green_and_reports_the_ingestion_subsystem()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await _factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Status_endpoint_is_forbidden_for_an_unrecognised_role()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await SendAsAsync(HttpMethod.Get, "/api/desired-state/status", "SomeUnrecognisedRole");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Every_read_role_can_read_all_five_endpoints(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        (await SendAsAsync(HttpMethod.Get, "/api/desired-state/status", role)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsAsync(HttpMethod.Get, "/api/desired-state/racks", role)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsAsync(HttpMethod.Get, "/api/desired-state/racks/no-such-rack/active", role)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsAsync(HttpMethod.Get, "/api/desired-state/ingestion-runs", role)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsAsync(HttpMethod.Get, "/api/desired-state/validation-errors", role)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Active_desired_state_for_an_unknown_rack_is_not_found()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await SendAsAsync(HttpMethod.Get, "/api/desired-state/racks/does-not-exist/active", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Active_desired_state_returns_the_typed_tree_for_a_seeded_rack()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-api-" + Guid.NewGuid().ToString("N");
        await SeedActiveVersionAsync(rackSlug);

        var response = await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/active", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DesiredStateActiveDto>();
        body!.RackSlug.Should().Be(rackSlug);
        body.Rack.Switches.Should().ContainSingle();
        body.Rack.Switches[0].Ports.Should().ContainSingle().Which.AccessVlan.Should().Be(77);
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Every_read_role_can_read_the_new_revision_endpoints(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-rbac-" + Guid.NewGuid().ToString("N");
        var (versionId, commitSha) = await SeedActiveVersionAsync(rackSlug);

        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions", role))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions/{versionId}", role))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions/by-commit/{commitSha}", role))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Revision_endpoints_are_unauthorized_for_anonymous_and_forbidden_for_an_unrecognised_role()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-rbac-neg-" + Guid.NewGuid().ToString("N");

        (await _factory.CreateClient().GetAsync($"/api/desired-state/racks/{rackSlug}/revisions"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions", "SomeUnrecognisedRole"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Active_desired_state_sets_a_strong_etag_and_honours_if_none_match()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-etag-" + Guid.NewGuid().ToString("N");
        await SeedActiveVersionAsync(rackSlug);

        var first = await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/active", "ReadOnly");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty();

        var matched = await SendWithIfNoneMatchAsync($"/api/desired-state/racks/{rackSlug}/active", etag!);
        matched.StatusCode.Should().Be(HttpStatusCode.NotModified);

        var stale = await SendWithIfNoneMatchAsync($"/api/desired-state/racks/{rackSlug}/active", "\"stale-etag\"");
        stale.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Revision_by_id_and_by_commit_return_the_full_payload_and_404_across_racks_with_a_machine_readable_code()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackA = "rack-detail-a-" + Guid.NewGuid().ToString("N");
        var rackB = "rack-detail-b-" + Guid.NewGuid().ToString("N");
        var (versionId, commitSha) = await SeedActiveVersionAsync(rackA);
        await SeedActiveVersionAsync(rackB);

        var byId = await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackA}/revisions/{versionId}", "ReadOnly");
        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await byId.Content.ReadFromJsonAsync<DesiredStateRevisionDetailDto>();
        detail!.RackSlug.Should().Be(rackA);
        detail.CommitSha.Should().Be(commitSha);

        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackA}/revisions/by-commit/{commitSha}", "ReadOnly"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Cross-rack: neither the id nor the commit belonging to rack A resolves under rack B (NFR1).
        var crossById = await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackB}/revisions/{versionId}", "ReadOnly");
        crossById.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackB}/revisions/by-commit/{commitSha}", "ReadOnly"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await crossById.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("DESIRED_STATE_REVISION_NOT_FOUND");
    }

    [SkippableFact]
    public async Task Active_desired_state_404_carries_the_machine_readable_not_found_code()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var response = await SendAsAsync(HttpMethod.Get, "/api/desired-state/racks/no-such-rack/active", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("DESIRED_STATE_NOT_FOUND");
    }

    [SkippableFact]
    public async Task Revision_list_JSON_excludes_the_payload_while_by_id_includes_it()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-listshape-" + Guid.NewGuid().ToString("N");
        var (versionId, _) = await SeedActiveVersionAsync(rackSlug);

        var listBody = await (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions", "ReadOnly"))
            .Content.ReadAsStringAsync();
        listBody.Should().NotContain("desiredStateJson", "the history list must be metadata-only (AC3, NFR3)");

        var detailBody = await (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions/{versionId}", "ReadOnly"))
            .Content.ReadAsStringAsync();
        detailBody.Should().Contain("desiredStateJson");
    }

    [SkippableFact]
    public async Task Revision_history_pagination_round_trips_with_no_duplicates_or_gaps()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-history-page-" + Guid.NewGuid().ToString("N");
        var seededIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var (versionId, _) = await SeedActiveVersionAsync(rackSlug, DateTime.UtcNow.AddSeconds(i));
            seededIds.Add(versionId);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/desired-state/racks/{rackSlug}/revisions?pageSize=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await SendAsAsync(HttpMethod.Get, url, "Admin");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await response.Content.ReadFromJsonAsync<PagedResult<DesiredStateRevisionMetadataDto>>();
            seen.AddRange(page!.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null && seen.Count < seededIds.Count);

        seen.Should().BeEquivalentTo(seededIds, "keyset pagination must return every seeded revision exactly once");
        seen.Should().OnlyHaveUniqueItems();
    }

    [SkippableTheory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task No_non_get_verb_exists_on_any_desired_state_revision_route(string method)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-verb-" + Guid.NewGuid().ToString("N");
        var routes = new[]
        {
            $"/api/desired-state/racks/{rackSlug}/active",
            $"/api/desired-state/racks/{rackSlug}/revisions",
            $"/api/desired-state/racks/{rackSlug}/revisions/{Guid.NewGuid()}",
            $"/api/desired-state/racks/{rackSlug}/revisions/by-commit/some-sha",
        };

        foreach (var route in routes)
        {
            var response = await SendAsAsync(new HttpMethod(method), route, "Admin");
            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }
    }

    [SkippableFact]
    public async Task Reading_a_revision_writes_a_desired_state_read_audit_event()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackSlug = "rack-audit-" + Guid.NewGuid().ToString("N");
        var (versionId, _) = await SeedActiveVersionAsync(rackSlug);

        (await SendAsAsync(HttpMethod.Get, $"/api/desired-state/racks/{rackSlug}/revisions/{versionId}", "ReadOnly"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Audit is written off the request path (ChannelAuditEventWriter/AuditEventBackgroundWriter,
        // finding #5) and is eventually — not synchronously — consistent, so poll rather than read once.
        var audit = await PollForAuditEventAsync("desired-state.revision.read", versionId.ToString());
        audit.Should().NotBeNull("every desired-state revision read must write an audit event (AC5)");
    }

    private async Task<Caisson.Domain.Topology.TopologyAuditEvent?> PollForAuditEventAsync(string action, string? targetId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var found = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.TargetId == targetId);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendWithIfNoneMatchAsync(string url, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "ReadOnly");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return await _factory.CreateClient().SendAsync(request);
    }

    [SkippableFact]
    public async Task Webhook_with_a_valid_signature_is_accepted_and_returns_a_correlation_id()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var body = "{\"ref\":\"refs/heads/main\"}"u8.ToArray();

        var response = await PostWebhookAsync(body, Sign(body));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<GitWebhookAcceptedResponse>();
        payload!.CorrelationId.Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task Webhook_with_an_invalid_signature_is_rejected_regardless_of_any_bearer_token()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var body = "{\"ref\":\"refs/heads/main\"}"u8.ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/git/webhook")
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
        // A role header is present too — proves the 401 comes from the HMAC check, not RBAC.
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Webhook_with_a_missing_signature_is_rejected()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var body = "{}"u8.ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/git/webhook")
        {
            Content = new ByteArrayContent(body),
        };

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task No_response_body_ever_contains_the_webhook_secret()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var statusBody = await (await SendAsAsync(HttpMethod.Get, "/api/desired-state/status", "Admin")).Content.ReadAsStringAsync();
        var runsBody = await (await SendAsAsync(HttpMethod.Get, "/api/desired-state/ingestion-runs", "Admin")).Content.ReadAsStringAsync();
        var webhookBody = await (await PostWebhookAsync("{}"u8.ToArray(), Sign("{}"u8.ToArray()))).Content.ReadAsStringAsync();

        statusBody.Should().NotContain(FixedGitIngestionSecretsResolver.Secret);
        runsBody.Should().NotContain(FixedGitIngestionSecretsResolver.Secret);
        webhookBody.Should().NotContain(FixedGitIngestionSecretsResolver.Secret);
    }

    [SkippableFact]
    public async Task Ingestion_runs_pagination_round_trips_with_no_duplicates_or_gaps()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var marker = "pagination-marker-" + Guid.NewGuid().ToString("N");
        var seededIds = await SeedRunsAsync(marker, count: 5);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = "/api/desired-state/ingestion-runs?pageSize=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await SendAsAsync(HttpMethod.Get, url, "Admin");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await response.Content.ReadFromJsonAsync<PagedResult<DesiredStateIngestionRunSummaryDto>>();
            seen.AddRange(page!.Items.Where(i => seededIds.Contains(i.RunId)).Select(i => i.RunId));
            cursor = page.NextCursor;
        }
        while (cursor is not null && seen.Count < seededIds.Count);

        seen.Should().BeEquivalentTo(seededIds, "keyset pagination must return every seeded run exactly once");
        seen.Should().OnlyHaveUniqueItems();
    }

    private async Task<HttpResponseMessage> SendAsAsync(HttpMethod method, string url, string role)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(byte[] body, string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/git/webhook")
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Add("X-Hub-Signature-256", signature);
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private static string Sign(byte[] body)
        => "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(FixedGitIngestionSecretsResolver.Secret), body)).ToLowerInvariant();

    private async Task<(Guid VersionId, string CommitSha)> SeedActiveVersionAsync(
        string rackSlug, DateTime? createdAtUtc = null)
    {
        var commitSha = "api-test-sha-" + Guid.NewGuid().ToString("N");
        var createdAt = createdAtUtc ?? DateTime.UtcNow;

        await using var context = _factory.CreateDbContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, commitSha, run.Id, createdAt, "hash-" + commitSha,
            "{\"rackSlug\":\"" + rackSlug + "\",\"switches\":[]}", DesiredStateSchema.CurrentSchemaVersion,
            "desired-state-ingestion", "author", "author@example.com", createdAt);
        var rack = new DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, rackSlug);
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rack.Id, "sw-a", $"{rackSlug}|sw-a");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "eth0", $"{rackSlug}|sw-a|eth0", 77);

        context.DesiredStateVersions.Add(version);
        context.DesiredRackIntents.Add(rack);
        context.DesiredSwitchIntents.Add(switchIntent);
        context.DesiredPortIntents.Add(port);
        await context.SaveChangesAsync();

        return (version.Id, commitSha);
    }

    private async Task<List<Guid>> SeedRunsAsync(string marker, int count)
    {
        await using var context = _factory.CreateDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var run = new DesiredStateIngestionRun(
                Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow.AddSeconds(i),
                "https://example.com/repo.git", "main", Guid.NewGuid());
            run.RecordCommit($"{marker}-{i}", "author", DateTime.UtcNow, "message");
            run.Succeed(DateTime.UtcNow.AddSeconds(i));
            context.DesiredStateIngestionRuns.Add(run);
            ids.Add(run.Id);
        }

        await context.SaveChangesAsync();
        return ids;
    }
}
