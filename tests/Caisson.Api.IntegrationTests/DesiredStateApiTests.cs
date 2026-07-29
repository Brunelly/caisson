using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState;
using FluentAssertions;
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

    private async Task SeedActiveVersionAsync(string rackSlug)
    {
        await using var context = _factory.CreateDbContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit("api-test-sha", "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, "api-test-sha", run.Id, DateTime.UtcNow, "hash-" + rackSlug);
        var rack = new DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, rackSlug);
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rack.Id, "sw-a", $"{rackSlug}|sw-a");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "eth0", $"{rackSlug}|sw-a|eth0", 77);

        context.DesiredStateVersions.Add(version);
        context.DesiredRackIntents.Add(rack);
        context.DesiredSwitchIntents.Add(switchIntent);
        context.DesiredPortIntents.Add(port);
        await context.SaveChangesAsync();
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
