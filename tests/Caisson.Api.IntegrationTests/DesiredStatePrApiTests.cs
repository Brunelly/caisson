using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.Git;
using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end desired-state PR behaviour (stories #170 gate + #172 publisher): the #170 gate still blocks
/// stale/error/unacknowledged candidates before any write, and the #172 publisher creates/reuses/refuses/fails
/// against the in-memory <see cref="FakeGitHubPullRequestClient"/> + <see cref="FakeGitCredentialProvider"/>
/// (no real network/Azure). Covers create metadata, idempotent reuse, N-concurrent → 1 PR, the PR-only
/// guardrail 409, GitHub/credential failures, and that closed PRs do not masquerade as open reuse.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStatePrApiTests
{
    private const string RackSlug = "seed-preflight-rack";
    private readonly CaissonApiFactory _factory;

    // A unique actor per test method: the rate limiter partitions by the oid claim, so a distinct actor gives
    // each test its own NetworkConfigRoundTrip budget and its requests never starve other tests in the shared
    // host (which would otherwise surface as spurious 429s elsewhere in the collection).
    private readonly string _actor = "pr-" + Guid.NewGuid().ToString("N")[..12];

    public DesiredStatePrApiTests(CaissonApiFactory factory) => _factory = factory;

    // ---- RBAC (AC5) --------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Anonymous_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await _factory.CreateClient().PostAsJsonAsync(Path(Preflight.RackId),
            new CreatePrRequest("x", Array.Empty<string>(), Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("ReadOnly")]
    [InlineData("Operator")]
    public async Task Roles_without_the_author_permission_are_forbidden(string role)
    {
        Skip.IfNot(_factory.Available, SkipReason);

        // Story #172 reuses the existing NetworkConfigAuthor policy (ADR 0056): a caller must hold the author
        // permission. ReadOnly and Operator do not, so both are forbidden and no git/credential call is made.
        _factory.GitHub.Reset();
        _factory.Credentials.Reset();

        var response = await PostAsync(Preflight.RackId, role,
            new CreatePrRequest("x", Array.Empty<string>(), Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.GitHub.OpenPullRequestCalls.Should().Be(0);
        _factory.Credentials.Calls.Should().Be(0);
    }

    // ---- #170 gate still blocks before any write ---------------------------------------------------------

    [SkippableFact]
    public async Task A_blocking_error_is_rejected_with_a_structured_422_and_no_git_write()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();

        var runId = await ValidateForRunIdAsync(Preflight.RackId, ErrorCandidate());
        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), ErrorCandidate().VlanCatalogue!, ErrorCandidate().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("errors");
        _factory.GitHub.OpenPullRequestCalls.Should().Be(0);
    }

    [SkippableFact]
    public async Task A_stale_validation_run_id_is_rejected_with_revalidate()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest("deadbeef", Array.Empty<string>(), ValidRequest("data").VlanCatalogue!, ValidRequest("data").PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("revalidate");
    }

    [SkippableFact]
    public async Task An_unacknowledged_safety_warning_is_rejected()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var runId = await ValidateForRunIdAsync(Preflight.RackId, UplinkChangeRequest());
        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), UplinkChangeRequest().VlanCatalogue!, UplinkChangeRequest().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("acknowledge-warnings");
    }

    // ---- Create (AC1) ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_valid_candidate_creates_a_pr_with_full_metadata_and_audit()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();
        _factory.Credentials.Reset();

        var candidate = ValidRequest("create-" + Suffix());
        var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);
        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = (await response.Content.ReadFromJsonAsync<CreatePrResponse>())!;
        body.Reused.Should().BeFalse();
        body.PullRequestUrl.Should().NotBeNullOrEmpty();
        body.PullRequestNumber.Should().NotBeNull();
        body.BranchName.Should().StartWith($"caisson/seed-preflight-rack/op-{_actor}/");
        body.CommitSha.Should().NotBeNullOrEmpty();
        body.CandidateFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        body.RepoOwner.Should().Be("test-owner");
        body.RepoName.Should().Be("test-repo");
        body.ChangeSummary!.Total.Should().BeGreaterThan(0);

        // Exactly one PR opened, credential fetched, file committed at the ingestion read path.
        _factory.GitHub.OpenPullRequestCalls.Should().Be(1);
        _factory.Credentials.Calls.Should().BeGreaterThan(0);
        _factory.GitHub.LastCommitPath.Should().Be("desired-state/racks/seed-preflight-rack.yaml");

        // Title + machine-readable body block carry the evidence.
        _factory.GitHub.LastTitle.Should().Be($"Rack seed-preflight-rack: network desired-state update ({_actor})");
        _factory.GitHub.LastBody.Should().Contain("```json").And.Contain(body.CandidateFingerprint!).And.Contain(_actor);
        using (var doc = JsonDocument.Parse(ExtractJsonBlock(_factory.GitHub.LastBody!)))
        {
            doc.RootElement.GetProperty("rack").GetString().Should().Be("seed-preflight-rack");
            doc.RootElement.GetProperty("candidateFingerprint").GetString().Should().Be(body.CandidateFingerprint);
            doc.RootElement.TryGetProperty("changeSummary", out _).Should().BeTrue();
        }

        // Persisted link + audit; no secret/YAML in the audit details.
        var link = await FindOpenLinkAsync(Preflight.RackId, body.CandidateFingerprint!);
        link.Should().NotBeNull();
        link!.PullRequestUrl.Should().Be(body.PullRequestUrl);

        var audit = await PollForAuditEventAsync("git.pr.created", Preflight.RackId);
        audit.DetailsJson.Should().Contain(body.CandidateFingerprint!).And.Contain("prUrl");
        audit.DetailsJson.Should().NotContain("fake-pat-token");
        audit.DetailsJson.Should().NotContain("vlanCatalogue");
    }

    // ---- Idempotent reuse (AC2) --------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_identical_repeat_reuses_the_open_pr_without_a_second_github_or_credential_call()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();
        _factory.Credentials.Reset();

        var candidate = ValidRequest("reuse-" + Suffix());
        var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);

        var first = (await (await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!)))
            .Content.ReadFromJsonAsync<CreatePrResponse>())!;
        first.Reused.Should().BeFalse();

        var githubCallsAfterCreate = _factory.GitHub.GetRepositoryCalls;
        var credentialCallsAfterCreate = _factory.Credentials.Calls;

        var second = (await (await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!)))
            .Content.ReadFromJsonAsync<CreatePrResponse>())!;

        second.Reused.Should().BeTrue();
        second.PullRequestNumber.Should().Be(first.PullRequestNumber);
        second.PullRequestUrl.Should().Be(first.PullRequestUrl);

        // No new PR, no new GitHub/credential traffic on the reuse (AC2 latency path).
        _factory.GitHub.OpenPullRequestCalls.Should().Be(1);
        _factory.GitHub.GetRepositoryCalls.Should().Be(githubCallsAfterCreate);
        _factory.Credentials.Calls.Should().Be(credentialCallsAfterCreate);

        (await CountLinksAsync(Preflight.RackId, first.CandidateFingerprint!)).Should().Be(1);
        await PollForAuditEventAsync("git.pr.reused", Preflight.RackId);
    }

    [SkippableFact]
    public async Task Different_candidates_create_distinct_branches_and_prs()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();

        var a = ValidRequest("distinct-a-" + Suffix());
        var b = ValidRequest("distinct-b-" + Suffix());

        var ra = (await CreateAsync(a))!;
        var rb = (await CreateAsync(b))!;

        ra.CandidateFingerprint.Should().NotBe(rb.CandidateFingerprint);
        ra.BranchName.Should().NotBe(rb.BranchName);
        ra.PullRequestNumber.Should().NotBe(rb.PullRequestNumber);
        _factory.GitHub.OpenPullRequestCalls.Should().Be(2);
    }

    // ---- Concurrency (NFR3) ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Five_concurrent_identical_requests_yield_one_pr_and_four_reuses()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();

        var candidate = ValidRequest("concurrent-" + Suffix());
        var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);
        var request = new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => PostAsync(Preflight.RackId, "NetworkConfigAuthor", request)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<CreatePrResponse>()));

        _factory.GitHub.OpenPullRequestCalls.Should().Be(1);
        bodies.Count(b => b!.Reused == false).Should().Be(1);
        bodies.Count(b => b!.Reused).Should().Be(4);
        (await CountLinksAsync(Preflight.RackId, bodies[0]!.CandidateFingerprint!)).Should().Be(1);
    }

    // ---- PR-only guardrail (AC3) -------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_branch_equal_to_the_default_branch_is_refused_with_409_and_no_write()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();

        var candidate = ValidRequest("guardrail-" + Suffix());
        var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);

        // Pin the clock so the generated branch is deterministic, then make the repository's default branch
        // equal to exactly that branch — the authoritative guardrail must refuse before any write.
        var pinned = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        _factory.Clock.Pinned = pinned;
        try
        {
            var fingerprint = ComputeFingerprint(candidate);
            var branch = PrBranchNaming.Build(RackSlug, _actor, fingerprint, pinned.UtcDateTime, "caisson");
            _factory.GitHub.DefaultBranch = branch;

            var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
                new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!));

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ErrorCodeAsync(response)).Should().Be(GitPrErrorCodes.PrOnlyGuardrailViolation);
            _factory.GitHub.CreateBranchCalls.Should().Be(0);
            _factory.GitHub.OpenPullRequestCalls.Should().Be(0);

            var audit = await PollForAuditEventAsync("git.pr.refused_pr_only", Preflight.RackId);
            audit.DetailsJson.Should().Contain(GitPrErrorCodes.PrOnlyGuardrailViolation);
        }
        finally
        {
            _factory.Clock.Pinned = null;
            _factory.GitHub.DefaultBranch = "main";
        }
    }

    // ---- Failure paths (AC6) -----------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_github_failure_returns_a_stable_error_code_and_audits_without_leaking_secrets()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();
        _factory.Credentials.Reset();
        _factory.GitHub.FailOnOpen = true;

        try
        {
            var candidate = ValidRequest("ghfail-" + Suffix());
            var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);
            var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
                new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!));

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await ErrorCodeAsync(response)).Should().Be(GitPrErrorCodes.GitHubApiFailed);

            var audit = await PollForAuditEventAsync("git.pr.failed", Preflight.RackId);
            audit.DetailsJson.Should().Contain(GitPrErrorCodes.GitHubApiFailed);
            audit.DetailsJson.Should().NotContain("fake-pat-token");
            audit.DetailsJson.Should().NotContain("vlanCatalogue");
        }
        finally
        {
            _factory.GitHub.FailOnOpen = false;
        }
    }

    [SkippableFact]
    public async Task An_unavailable_credential_returns_a_stable_error_code()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();
        _factory.Credentials.Reset();
        _factory.Credentials.FailUnavailable = true;

        try
        {
            var candidate = ValidRequest("credfail-" + Suffix());
            var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);
            var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
                new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!));

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await ErrorCodeAsync(response)).Should().Be(GitPrErrorCodes.GitCredentialsUnavailable);
            _factory.GitHub.OpenPullRequestCalls.Should().Be(0);
        }
        finally
        {
            _factory.Credentials.FailUnavailable = false;
        }
    }

    [SkippableFact]
    public async Task A_closed_prior_pr_does_not_masquerade_as_an_open_reuse()
    {
        Skip.IfNot(_factory.Available, SkipReason);
        _factory.GitHub.Reset();

        var candidate = ValidRequest("closed-" + Suffix());
        var first = (await CreateAsync(candidate))!;
        first.Reused.Should().BeFalse();

        // Close the open link out-of-band, then repeat the identical candidate.
        await CloseLinkAsync(Preflight.RackId, first.CandidateFingerprint!);

        var second = (await CreateAsync(candidate))!;
        second.Reused.Should().BeFalse();
        second.PullRequestNumber.Should().NotBe(first.PullRequestNumber);
        _factory.GitHub.OpenPullRequestCalls.Should().Be(2);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private SeededPreflight Preflight => _factory.Seed.Preflight;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private PreflightValidateRequest ValidRequest(string vlanName)
        => new(
            new[] { new VlanCatalogueEntryDto(10, vlanName, null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, Preflight.AccessPortName, 10) });

    private PreflightValidateRequest UplinkChangeRequest()
        => new(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, Preflight.UplinkPortName, 10) });

    private PreflightValidateRequest ErrorCandidate()
        => new(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, "ether-nope", 10) });

    private static string ComputeFingerprint(PreflightValidateRequest candidate)
    {
        var (vlans, ports) = PreflightContractMappers.ToDomain(candidate.VlanCatalogue, candidate.PortIntents);
        var model = new SupportedDesiredStateModel(RackSlug, vlans, ports);
        return DesiredStateContentHash.Compute(DesiredStateYamlRenderer.Render(model).Yaml);
    }

    private async Task<CreatePrResponse?> CreateAsync(PreflightValidateRequest candidate)
    {
        var runId = await ValidateForRunIdAsync(Preflight.RackId, candidate);
        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), candidate.VlanCatalogue!, candidate.PortIntents!));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return await response.Content.ReadFromJsonAsync<CreatePrResponse>();
    }

    private async Task<string> ValidateForRunIdAsync(Guid rackId, PreflightValidateRequest candidate)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/racks/{rackId}/desired-state/preflight-validate")
        {
            Content = JsonContent.Create(candidate),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _actor);
        request.Headers.Add(TestAuthHandler.RolesHeader, "NetworkConfigAuthor");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        return body!.ValidationRunId;
    }

    private async Task<HttpResponseMessage> PostAsync(Guid rackId, string role, CreatePrRequest body)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Path(rackId)) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.UserHeader, _actor);
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReasonCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("reasonCode", out var value) ? value.GetString() : null;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
    }

    private static string ExtractJsonBlock(string body)
    {
        const string fence = "```json";
        var start = body.IndexOf(fence, StringComparison.Ordinal) + fence.Length;
        var end = body.IndexOf("```", start, StringComparison.Ordinal);
        return body[start..end].Trim();
    }

    private async Task<GitPullRequestLink?> FindOpenLinkAsync(Guid rackId, string fingerprint)
    {
        await using var context = _factory.CreateDbContext();
        return await context.GitPullRequestLinks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RackId == rackId && x.CandidateFingerprint == fingerprint
                && x.Status == GitPullRequestStatus.Open);
    }

    private async Task<int> CountLinksAsync(Guid rackId, string fingerprint)
    {
        await using var context = _factory.CreateDbContext();
        return await context.GitPullRequestLinks.CountAsync(x => x.RackId == rackId && x.CandidateFingerprint == fingerprint);
    }

    private async Task CloseLinkAsync(Guid rackId, string fingerprint)
    {
        await using var context = _factory.CreateDbContext();
        var link = await context.GitPullRequestLinks
            .FirstAsync(x => x.RackId == rackId && x.CandidateFingerprint == fingerprint
                && x.Status == GitPullRequestStatus.Open);
        link.UpdateStatus(GitPullRequestStatus.Closed, DateTime.UtcNow);
        await context.SaveChangesAsync();
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

    private static string Path(Guid rackId) => $"/api/racks/{rackId}/desired-state/prs";

    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";
}
