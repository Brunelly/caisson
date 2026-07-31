using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end impact-preview behaviour (story #171, Tasks #200/#202): baseline = latest ingested revision,
/// raw + structured diff, per-content caching (hit/miss/new-baseline), RBAC + leak-safe cross-rack scoping,
/// invalid-YAML 400 (no cache row), missing-baseline 409, and counts-only audit. Mirrors
/// <see cref="DesiredStatePreflightApiTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStateImpactPreviewApiTests
{
    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";

    private readonly CaissonApiFactory _factory;

    public DesiredStateImpactPreviewApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var response = await _factory.CreateClient()
            .PostAsJsonAsync(Path(rackId), new { yaml = CandidateYaml(slug, 20) });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("ReadOnly")]
    [InlineData("Operator")]
    public async Task A_reader_or_operator_gets_the_raw_and_structured_diff_against_the_latest_revision(string role)
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, versionId) = await SeedBaselineAsync();

        var response = await PostAsync(rackId, role, CandidateYaml(slug, 20));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ImpactPreviewResponse>();
        body!.BaselineRevisionId.Should().Be(versionId);
        body.RawUnifiedDiff.Should().NotBeNullOrEmpty();
        body.CacheHit.Should().BeFalse();
        body.CandidateSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        // Baseline eth1=10, candidate eth1=20 + vlan 20 -> a port change and VLAN add/remove.
        body.PortChanges.Should().Contain(c => c.Summary == "Switch sw1 Port eth1 accessVlan changed 10→20");
        body.VlanChanges.Should().Contain(c => c.Summary == "VLAN 20 added");
    }

    [SkippableFact]
    public async Task An_identical_second_request_is_served_from_cache_with_the_same_candidate_id()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var first = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));
        var second = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));

        first.CacheHit.Should().BeFalse();
        second.CacheHit.Should().BeTrue();
        second.CandidateId.Should().Be(first.CandidateId);
        second.CreatedAtUtc.Should().Be(first.CreatedAtUtc);
        second.RawUnifiedDiff.Should().Be(first.RawUnifiedDiff);
        (await CountCacheRowsAsync(rackId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task A_formatting_only_edit_canonicalizes_to_the_same_cache_row()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var first = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));
        // Same semantic content, but with a comment + extra blank line -> identical canonical YAML.
        var reformatted = CandidateYaml(slug, 20) + "\n# a trailing comment\n";
        var second = await ReadAsync(await PostAsync(rackId, "Operator", reformatted));

        second.CandidateId.Should().Be(first.CandidateId);
        second.CacheHit.Should().BeTrue();
        (await CountCacheRowsAsync(rackId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Modified_content_yields_a_new_candidate_id_and_does_not_reuse_the_cache()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var first = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));
        var second = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 30)));

        second.CandidateId.Should().NotBe(first.CandidateId);
        (await CountCacheRowsAsync(rackId)).Should().Be(2);
    }

    [SkippableFact]
    public async Task A_new_baseline_revision_invalidates_the_previous_preview()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, firstVersionId) = await SeedBaselineAsync();

        var first = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));
        first.BaselineRevisionId.Should().Be(firstVersionId);

        var secondVersionId = await SeedRevisionAsync(rackId, slug, accessVlan: 15);
        var second = await ReadAsync(await PostAsync(rackId, "Operator", CandidateYaml(slug, 20)));

        second.BaselineRevisionId.Should().Be(secondVersionId);
        second.CacheHit.Should().BeFalse(); // a new baseline key -> a fresh computation
        second.CandidateId.Should().NotBe(first.CandidateId);
    }

    [SkippableFact]
    public async Task Invalid_yaml_returns_400_with_positions_and_writes_no_cache_row()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, _, _) = await SeedBaselineAsync();

        var response = await PostAsync(rackId, "Operator", "apiVersion: caisson.dev/v1alpha1\nspec: {unterminated");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.TryGetProperty("issues", out var issues).Should().BeTrue();
        issues.GetArrayLength().Should().BeGreaterThan(0);
        (await CountCacheRowsAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task A_rack_with_no_baseline_returns_409_with_a_reason_code()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var rackId = await _factory.CreateRackWithExternalKeyAsync(UniqueSlug());

        var response = await PostAsync(rackId, "Operator", CandidateYaml("rack-x", 20));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<MissingBaselineResponse>();
        body!.ReasonCode.Should().Be("DESIRED_STATE_BASELINE_MISSING");
        body.Message.Should().Contain("ingest");
        (await CountCacheRowsAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Get_by_candidate_returns_the_cached_preview_and_is_rack_scoped()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackA, slugA, _) = await SeedBaselineAsync();
        var (rackB, _, _) = await SeedBaselineAsync();

        var created = await ReadAsync(await PostAsync(rackA, "Operator", CandidateYaml(slugA, 20)));

        // Same rack -> 200.
        var onRackA = await GetAsync(rackA, created.CandidateId, "Operator");
        onRackA.StatusCode.Should().Be(HttpStatusCode.OK);

        // A candidate id from rack A must NOT resolve under rack B (no cross-rack leak, NFR2).
        var onRackB = await GetAsync(rackB, created.CandidateId, "Operator");
        onRackB.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Concurrent_identical_requests_produce_a_single_artifact()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => PostAsync(rackId, "Operator", CandidateYaml(slug, 20)))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(responses.Select(ReadAsync));
        bodies.Select(b => b.CandidateId).Distinct().Should().ContainSingle();
        (await CountCacheRowsAsync(rackId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Every_request_is_audited_with_counts_and_hashes_only_and_no_payload_body()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        var (rackId, slug, _) = await SeedBaselineAsync();

        var response = await PostAsync(rackId, "Operator", CandidateYaml(slug, 20));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await PollForAuditEventAsync("desired-state.impact-previewed", rackId);
        audit.DetailsJson.Should().Contain("cacheHit").And.Contain("candidateSha256").And.Contain("outcome");
        audit.DetailsJson.Should().NotContain("ether").And.NotContain("vlanCatalogue").And.NotContain("accessVlan");
    }

    // ---- helpers ----

    private static string CandidateYaml(string rackSlug, int accessVlan) => $"""
        apiVersion: caisson.dev/v1alpha1
        kind: RackDesiredState
        metadata:
          rackSlug: {rackSlug}
        spec:
          vlans:
            - vlanId: {accessVlan}
              name: prod
          switches:
            - name: sw1
              ports:
                - name: eth1
                  accessVlan: {accessVlan}
        """;

    private static string BaselineJson(string rackSlug, int accessVlan)
        => $$"""{"rackSlug":"{{rackSlug}}","switches":[{"name":"sw1","ports":[{"name":"eth1","accessVlan":{{accessVlan}},"description":null,"neighborSystemName":null,"neighborPortId":null}]}]}""";

    private static string UniqueSlug() => "rack-ip-" + Guid.NewGuid().ToString("N")[..24];

    private async Task<(Guid RackId, string Slug, Guid VersionId)> SeedBaselineAsync()
    {
        var slug = UniqueSlug();
        var rackId = await _factory.CreateRackWithExternalKeyAsync(slug);
        var versionId = await SeedRevisionAsync(rackId, slug, accessVlan: 10);
        return (rackId, slug, versionId);
    }

    private async Task<Guid> SeedRevisionAsync(Guid rackId, string slug, int accessVlan)
    {
        await using var context = _factory.CreateDbContext();

        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(Sha(), "author", DateTime.UtcNow, "seed baseline");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var versionId = Guid.NewGuid();
        context.DesiredStateVersions.Add(new DesiredStateVersion(
            versionId, slug, Sha(), run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"),
            BaselineJson(slug, accessVlan), 1, "desired-state-ingestion"));
        await context.SaveChangesAsync();
        return versionId;
    }

    private static string Sha() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private async Task<int> CountCacheRowsAsync(Guid rackId)
    {
        await using var context = _factory.CreateDbContext();
        return await context.DesiredStateCandidateDiffCaches.CountAsync(c => c.RackId == rackId);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid rackId, string role, string yaml)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path(rackId))
        {
            Content = JsonContent.Create(new { yaml }),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid rackId, Guid candidateId, string role)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/racks/{rackId}/desired-state/candidates/{candidateId}/impact-preview");
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<ImpactPreviewResponse> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ImpactPreviewResponse>())!;
    }

    private async Task<Caisson.Domain.Topology.TopologyAuditEvent> PollForAuditEventAsync(string action, Guid rackId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var audit = await context.AuditEvents
                .Where(a => a.Action == action && a.RackId == rackId)
                .OrderByDescending(a => a.OccurredAtUtc)
                .FirstOrDefaultAsync();
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"No audit event action={action} rackId={rackId} appeared within the test budget.");
    }

    private static string Path(Guid rackId) => $"/api/racks/{rackId}/desired-state/impact-preview";
}
