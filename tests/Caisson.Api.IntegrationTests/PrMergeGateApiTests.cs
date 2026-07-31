using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end tests for the merged-apply gate enforced at the drift-apply API (story #173, Task #213, AC4):
/// a blocked apply returns 409 with the exact PascalCase reason code and creates no job; an unrelated merged
/// PR does not unlock a different candidate; a merged exact candidate allows apply.
/// </summary>
public sealed class PrMergeGateApiTests : IAsyncLifetime
{
    private readonly PostgresHarness _postgres = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [SkippableFact]
    public async Task No_linked_pr_blocks_apply_with_409_no_pr_linked_and_creates_no_job()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var (rackId, itemId, _) = await SeedDriftAsync(desiredVlan: 150, observedVlan: 10);
        await using var host = NewHost();

        var response = await PostApplyAsync(host, rackId, itemId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReasonCodeAsync(response)).Should().Be(GitPrGateReasonCodes.NoPrLinked);
        (await CountJobsAsync(rackId, itemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Open_pr_blocks_apply_with_409_pr_not_merged()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var (rackId, itemId, contentHash) = await SeedDriftAsync(desiredVlan: 151, observedVlan: 10);
        await SeedPrLinkAsync(rackId, contentHash, GitPullRequestStatus.Open);
        await using var host = NewHost();

        var response = await PostApplyAsync(host, rackId, itemId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReasonCodeAsync(response)).Should().Be(GitPrGateReasonCodes.PrNotMerged);
        (await CountJobsAsync(rackId, itemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task An_unrelated_merged_pr_does_not_unlock_the_candidate()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var (rackId, itemId, _) = await SeedDriftAsync(desiredVlan: 152, observedVlan: 10);
        // A merged PR exists on the rack, but for a DIFFERENT (unrelated) fingerprint.
        await SeedPrLinkAsync(rackId, Hex(), GitPullRequestStatus.Merged);
        await using var host = NewHost();

        var response = await PostApplyAsync(host, rackId, itemId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReasonCodeAsync(response)).Should().Be(GitPrGateReasonCodes.NoPrLinked);
        (await CountJobsAsync(rackId, itemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Merged_exact_candidate_allows_apply()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var (rackId, itemId, contentHash) = await SeedDriftAsync(desiredVlan: 153, observedVlan: 10);
        await SeedPrLinkAsync(rackId, contentHash, GitPullRequestStatus.Merged);
        await using var host = NewHost();

        var response = await PostApplyAsync(host, rackId, itemId);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Accepted);
        (await CountJobsAsync(rackId, itemId)).Should().Be(1);
    }

    private static async Task<string> ReasonCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("reasonCode").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostApplyAsync(ApiHost host, Guid rackId, Guid driftItemId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply")
        {
            Content = JsonContent.Create(new ApplyDriftCorrectionRequest(driftItemId)),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "DriftApply");
        return await host.CreateClient().SendAsync(request);
    }

    private async Task SeedPrLinkAsync(Guid rackId, string fingerprint, GitPullRequestStatus state)
    {
        await using var context = _postgres.CreateContext();
        var linkId = Guid.NewGuid();
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/" + Guid.NewGuid().ToString("N")[..8],
            fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(Random.Shared.Next(1, 100000), "https://gh/pr/x", "commitshax", DateTime.UtcNow);
        if (state != GitPullRequestStatus.Open)
        {
            link.UpdateStatus(state, DateTime.UtcNow);
        }

        var record = new GitPullRequestStatusRecord(
            Guid.NewGuid(), linkId, rackId, "octo", "repo", 1, "https://gh/pr/x", DateTime.UtcNow);
        record.ApplyObservation(state, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", DateTime.UtcNow);

        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(record);
        await context.SaveChangesAsync();
    }

    /// <summary>Seeds a rack with one AccessVlanMismatch drift item and returns the item's desired-revision ContentHash.</summary>
    private async Task<(Guid RackId, Guid DriftItemId, string ContentHash)> SeedDriftAsync(int desiredVlan, int observedVlan)
    {
        var rackId = Guid.NewGuid();
        var rackSlug = "rack-" + rackId.ToString("N");
        await using (var context = _postgres.CreateContext())
        {
            context.Racks.Add(new Rack(rackId, rackSlug, "Gate Test Rack", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await IngestSnapshotAsync(rackId, observedVlan);
        await SeedDesiredStateAsync(rackSlug, desiredVlan);
        await RecomputeDriftAsync(rackId);

        await using var verify = _postgres.CreateContext();
        var report = await verify.DriftReports.OrderByDescending(r => r.ComputedAtUtc).FirstAsync(r => r.RackId == rackId);
        var item = await verify.DriftItems.FirstAsync(i => i.DriftReportId == report.Id && i.DriftType == DriftType.AccessVlanMismatch);
        var contentHash = await verify.DesiredStateVersions.Where(v => v.Id == report.DesiredRevisionId).Select(v => v.ContentHash).FirstAsync();
        return (rackId, item.DriftItemId, contentHash);
    }

    private async Task IngestSnapshotAsync(Guid rackId, int observedVlan)
    {
        var ports = new List<SwitchPortInfo> { new("ether1", true, observedVlan, Array.Empty<int>()) };
        var sw = new SwitchTopologySnapshot(
            "sw1", new SwitchDeviceInfo("10.0.0.1", "SW-1", "CRS354", "7.15"), ports,
            new List<LldpNeighbourInfo>(), new List<BridgeHostEntry>(), new List<VlanInfo> { new(observedVlan, "data") });
        var observed = new TopologyCorrelationInput(new[] { sw }, Array.Empty<ServerNicSnapshot>());
        var correlation = new TopologyCorrelationResult(
            Array.Empty<NicPortMapping>(), Array.Empty<AmbiguousNicMapping>(),
            Array.Empty<UnmappedNic>(), Array.Empty<UnmappedPort>());

        var request = new TopologyIngestionRequest(
            rackId, observed, correlation, TriggerType.OnDemand, "test", ActorType.ServiceAccount,
            "chr", "7.15", Guid.NewGuid(), SnapshotStatus.Completed, DateTime.UtcNow, DateTime.UtcNow);

        await using var context = _postgres.CreateContext();
        var service = new TopologySnapshotIngestionService(
            context, new GuidTopologyIdGenerator(), new NoOpTopologyEventPublisher(),
            new NoOpDriftRecomputeSignal(), NullLogger<TopologySnapshotIngestionService>.Instance);
        await service.IngestAsync(request);
    }

    private async Task SeedDesiredStateAsync(string rackSlug, int desiredVlan)
    {
        await using var context = _postgres.CreateContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main", Guid.NewGuid());
        run.RecordCommit("a".PadLeft(40, '0'), "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, "a".PadLeft(40, '0'), run.Id, DateTime.UtcNow,
            "hash-" + Guid.NewGuid().ToString("N"), "{}", 1, "desired-state-ingestion");
        var rackIntent = new DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, "rack-key");
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rackIntent.Id, "sw1", "switch-key");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "ether1", "port-key", accessVlan: desiredVlan);

        context.DesiredStateVersions.Add(version);
        context.DesiredRackIntents.Add(rackIntent);
        context.DesiredSwitchIntents.Add(switchIntent);
        context.DesiredPortIntents.Add(port);
        await context.SaveChangesAsync();
    }

    private async Task RecomputeDriftAsync(Guid rackId)
    {
        await using var context = _postgres.CreateContext();
        var service = new DriftComputationService(
            context, new GuidTopologyIdGenerator(), TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new Caisson.Drift.DriftComputationOptions()),
            NullLogger<DriftComputationService>.Instance);
        await service.ComputeAndPersistAsync(rackId, Guid.NewGuid());
    }

    private async Task<int> CountJobsAsync(Guid rackId, Guid driftItemId)
    {
        await using var context = _postgres.CreateContext();
        return await context.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == driftItemId);
    }

    private static string Hex() => (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];

    private ApiHost NewHost() => new(_postgres.ConnectionString);

    private sealed class ApiHost : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public ApiHost(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Caisson", _connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CaissonDbContext>));
                services.RemoveAll(typeof(DbContextOptions));
                services.AddDbContext<CaissonDbContext>(options => options.UseNpgsql(_connectionString));

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                // Real PrMergeGate (the subject under test); disable the drift-apply runner so an allowed apply
                // creates a job without a live driver call.
                services.Configure<DriftApplyOrchestrationOptions>(options => options.RunnerEnabled = false);
                services.Configure<DiscoveryOrchestrationOptions>(options =>
                {
                    options.RunnerEnabled = false;
                    options.SchedulerEnabled = false;
                });

                services.RemoveAll(typeof(Caisson.Ingestion.Git.ReadOnly.IGitRepositoryProvider));
                services.AddSingleton<Caisson.Ingestion.Git.ReadOnly.IGitRepositoryProvider, StubGitRepositoryProvider>();
                services.RemoveAll(typeof(Caisson.Ingestion.Security.IGitIngestionSecretsResolver));
                services.AddSingleton<Caisson.Ingestion.Security.IGitIngestionSecretsResolver, FixedGitIngestionSecretsResolver>();
            });
        }
    }
}
