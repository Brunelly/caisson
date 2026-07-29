using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using Caisson.Drift;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
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
/// End-to-end drift-apply behaviour (story #65) — the first write endpoint in the API. Each test builds
/// its own isolated Postgres-backed host (mirrors <c>TopologyEventFanOutTests</c>) so RBAC/validation
/// tests never race a live job runner, while happy-path/withheld-confirmation tests configure the
/// scripted driver BEFORE posting so the live runner's processing is fully deterministic.
/// </summary>
public sealed class DriftApplyApiTests : IAsyncLifetime
{
    private readonly PostgresHarness _postgres = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [SkippableFact]
    public async Task Anonymous_apply_is_unauthorized_and_creates_no_job()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 199, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/racks/{rackId}/drift/apply", new ApplyDriftCorrectionRequest(itemId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountJobsAsync(rackId, itemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Operator_without_drift_apply_permission_is_forbidden_and_creates_no_job()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 198, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, rackId, itemId, "Operator");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountJobsAsync(rackId, itemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Drift_apply_holder_creates_a_job_and_a_creation_audit_event()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 197, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, rackId, itemId, "DriftApply");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<ApplyDriftCorrectionResponse>();
        body!.JobId.Should().NotBeEmpty();
        response.Headers.Location!.ToString().Should().Contain($"/api/racks/{rackId}/jobs/{body.JobId}");

        await using var context = _postgres.CreateContext();
        var job = await context.DriftApplyJobs.FirstAsync(j => j.Id == body.JobId);
        job.RackId.Should().Be(rackId);
        job.DriftItemId.Should().Be(itemId);

        // ChannelAuditEventWriter is off-request-path (finding #5) — the row appears once
        // AuditEventBackgroundWriter's next flush (<=500ms) runs, not synchronously on response.
        var audit = await PollForAuditEventAsync("drift.apply.job.created", body.JobId.ToString());
        audit.DetailsJson.Should().Contain("DriftApply").And.Contain("correlationId");
    }

    [SkippableTheory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Empty_drift_item_id_is_rejected_with_400(string emptyGuid)
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, _) = await SeedAccessVlanMismatchAsync(desiredVlan: 196, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, rackId, Guid.Parse(emptyGuid), "DriftApply");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Unknown_drift_item_id_404s()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, _) = await SeedAccessVlanMismatchAsync(desiredVlan: 195, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, rackId, Guid.NewGuid(), "DriftApply");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Unknown_rack_404s()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, Guid.NewGuid(), Guid.NewGuid(), "DriftApply");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Unsupported_drift_type_returns_422_with_a_reason_code_and_creates_no_job()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // ether2 is observed but has no desired-port intent, so its drift item is ExtraObservedEntity —
        // actionable, but not the supported AccessVlanMismatch type (AC2).
        var (rackId, _, extraObservedItemId) = await SeedRackWithSecondPortAsync(desiredVlan: 194, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var response = await PostApplyAsync(host, rackId, extraObservedItemId, "DriftApply");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("reasonCode").GetString().Should().Be("unsupported-drift-type");
        (await CountJobsAsync(rackId, extraObservedItemId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Happy_path_apply_completes_with_before_after_state_and_terminal_audit()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 121, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: true);
        host.MutatingDriverFactory.Behavior = request =>
            TestSwitchChangeOutcomes.Ok(request, SwitchChangeReasonCode.Applied);

        var response = await PostApplyAsync(host, rackId, itemId, "DriftApply");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Accepted);
        var jobId = (await response.Content.ReadFromJsonAsync<ApplyDriftCorrectionResponse>())!.JobId;

        var detail = await PollUntilTerminalAsync(host, rackId, jobId);

        detail.RootElement.GetProperty("status").GetString().Should().Be(nameof(DriftApplyJobStatus.Completed));
        detail.RootElement.GetProperty("deviceReasonCode").GetString().Should().Be(nameof(SwitchChangeReasonCode.Applied));
        detail.RootElement.GetProperty("beforeState").GetString().Should().NotBeNullOrEmpty();
        detail.RootElement.GetProperty("afterState").GetString().Should().NotBeNullOrEmpty();
        host.MutatingDriverFactory.CallCount.Should().Be(1);

        var audit = await PollForAuditEventAsync("drift.apply.job.completed", jobId.ToString());
        audit.Result.Should().Be(nameof(DriftApplyJobStatus.Completed));
    }

    [SkippableFact]
    public async Task Withheld_confirmation_fails_the_job_with_the_auto_rolled_back_reason_and_makes_one_device_call()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 122, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: true);
        host.MutatingDriverFactory.Behavior = request =>
            TestSwitchChangeOutcomes.Ok(request, SwitchChangeReasonCode.AutoRolledBack, confirmed: false);

        var response = await PostApplyAsync(host, rackId, itemId, "DriftApply");
        var jobId = (await response.Content.ReadFromJsonAsync<ApplyDriftCorrectionResponse>())!.JobId;

        var detail = await PollUntilTerminalAsync(host, rackId, jobId);

        detail.RootElement.GetProperty("status").GetString().Should().Be(nameof(DriftApplyJobStatus.Failed));
        detail.RootElement.GetProperty("deviceReasonCode").GetString().Should().Be(nameof(SwitchChangeReasonCode.AutoRolledBack));
        host.MutatingDriverFactory.CallCount.Should().Be(1, "a withheld confirmation must not be retried as a second device write");
    }

    [SkippableFact]
    public async Task Stale_drift_marks_the_job_stale_without_ever_calling_the_driver()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId, rackSlug) = await SeedAccessVlanMismatchWithSlugAsync(desiredVlan: 123, observedVlan: 10);
        // RunnerEnabled=false: the job is created but nothing auto-processes it, so mutating the desired
        // state below and then manually driving the orchestrator is fully deterministic (no race against
        // a live poller).
        await using var host = NewHost(driftApplyRunnerEnabled: false);
        host.MutatingDriverFactory.Behavior = _ => throw new InvalidOperationException("the driver must never be called for stale drift");

        var response = await PostApplyAsync(host, rackId, itemId, "DriftApply");
        var jobId = (await response.Content.ReadFromJsonAsync<ApplyDriftCorrectionResponse>())!.JobId;

        // Change the desired access VLAN so the next recompute supersedes this DriftItemId (AC3).
        await MutateDesiredAccessVlanAsync(rackSlug, newVlan: 200);
        await RecomputeDriftAsync(rackId);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            // A TRACKED query (not IDriftApplyJobService.GetJobAsync's AsNoTracking read shape): the
            // orchestrator mutates the job in place and saves through the SAME context, mirroring exactly
            // how the live DriftApplyJobRunner loads the job it is about to run.
            var scopedContext = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IDriftApplyOrchestrator>();
            var job = await scopedContext.DriftApplyJobs.Include(j => j.Steps).FirstAsync(j => j.Id == jobId);
            await orchestrator.RunAsync(job, default);
        }

        await using var context = _postgres.CreateContext();
        var finalJob = await context.DriftApplyJobs.FirstAsync(j => j.Id == jobId);
        finalJob.Status.Should().Be(DriftApplyJobStatus.StaleDrift);
        // The port is still drifting, but to a DIFFERENT desired VLAN now — a changed-anchors mismatch,
        // not an absent item (AC3's "Both" check: presence AND expected/actual equality).
        finalJob.ErrorCode.Should().Be(DriftApplyErrorCodes.DriftAnchorsMismatched);
        host.MutatingDriverFactory.CallCount.Should().Be(0);

        var readResponse = await GetAsync(host, $"/api/racks/{rackId}/jobs/{jobId}", "ReadOnly");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task ReadOnly_can_read_job_status_but_cannot_apply()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var (rackId, itemId) = await SeedAccessVlanMismatchAsync(desiredVlan: 124, observedVlan: 10);
        await using var host = NewHost(driftApplyRunnerEnabled: false);

        var forbidden = await PostApplyAsync(host, rackId, itemId, "ReadOnly");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var listResponse = await GetAsync(host, $"/api/racks/{rackId}/jobs", "ReadOnly");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> PostApplyAsync(ApiHost host, Guid rackId, Guid driftItemId, string role)
    {
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply")
        {
            Content = JsonContent.Create(new ApplyDriftCorrectionRequest(driftItemId)),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(ApiHost host, string path, string role)
    {
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> PollUntilTerminalAsync(ApiHost host, Guid rackId, Guid jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await GetAsync(host, $"/api/racks/{rackId}/jobs/{jobId}", "ReadOnly");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is nameof(DriftApplyJobStatus.Completed) or nameof(DriftApplyJobStatus.Failed)
                or nameof(DriftApplyJobStatus.StaleDrift) or nameof(DriftApplyJobStatus.Canceled))
            {
                return doc;
            }

            doc.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Drift-apply job '{jobId}' did not reach a terminal state within the test budget.");
    }

    /// <summary>
    /// Polls for an audit event: writes may land via <c>ChannelAuditEventWriter</c>'s off-request-path
    /// flush (finding #5, <c>&lt;=500ms</c>) or a runner's own direct <c>SaveChangesAsync</c> — neither is
    /// guaranteed visible synchronously right after the HTTP response returns.
    /// </summary>
    private async Task<TopologyAuditEvent> PollForAuditEventAsync(string action, string targetId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _postgres.CreateContext();
            var audit = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.TargetId == targetId);
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"No audit event action={action} targetId={targetId} appeared within the test budget.");
    }

    private async Task<int> CountJobsAsync(Guid rackId, Guid driftItemId)
    {
        await using var context = _postgres.CreateContext();
        return await context.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == driftItemId);
    }

    private ApiHost NewHost(bool driftApplyRunnerEnabled) => new(_postgres.ConnectionString, driftApplyRunnerEnabled);

    /// <summary>Seeds a rack with one switch port ("sw1"/"ether1") and a single AccessVlanMismatch drift item.</summary>
    private async Task<(Guid RackId, Guid DriftItemId)> SeedAccessVlanMismatchAsync(int desiredVlan, int observedVlan)
    {
        var (rackId, itemId, _) = await SeedAccessVlanMismatchWithSlugAsync(desiredVlan, observedVlan);
        return (rackId, itemId);
    }

    private async Task<(Guid RackId, Guid DriftItemId, string RackSlug)> SeedAccessVlanMismatchWithSlugAsync(int desiredVlan, int observedVlan)
    {
        var rackId = Guid.NewGuid();
        var rackSlug = "rack-" + rackId.ToString("N");
        await using (var context = _postgres.CreateContext())
        {
            context.Racks.Add(new Rack(rackId, rackSlug, "Drift Apply Test Rack", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await IngestSnapshotAsync(rackId, observedVlan, includeSecondPort: false);
        await SeedDesiredStateAsync(rackSlug, desiredVlan);
        await RecomputeDriftAsync(rackId);

        var itemId = await FindItemAsync(rackId, DriftType.AccessVlanMismatch);
        return (rackId, itemId, rackSlug);
    }

    /// <summary>Seeds a rack with a second observed port ("ether2") that has no desired counterpart (ExtraObservedEntity, AC2).</summary>
    private async Task<(Guid RackId, Guid AccessVlanItemId, Guid ExtraObservedItemId)> SeedRackWithSecondPortAsync(int desiredVlan, int observedVlan)
    {
        var rackId = Guid.NewGuid();
        var rackSlug = "rack-" + rackId.ToString("N");
        await using (var context = _postgres.CreateContext())
        {
            context.Racks.Add(new Rack(rackId, rackSlug, "Drift Apply Test Rack", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await IngestSnapshotAsync(rackId, observedVlan, includeSecondPort: true);
        await SeedDesiredStateAsync(rackSlug, desiredVlan);
        await RecomputeDriftAsync(rackId);

        var accessVlanItemId = await FindItemAsync(rackId, DriftType.AccessVlanMismatch);
        var extraObservedItemId = await FindItemAsync(rackId, DriftType.ExtraObservedEntity);
        return (rackId, accessVlanItemId, extraObservedItemId);
    }

    private async Task<Guid> FindItemAsync(Guid rackId, DriftType driftType)
    {
        await using var context = _postgres.CreateContext();
        var report = await context.DriftReports.OrderByDescending(r => r.ComputedAtUtc).FirstAsync(r => r.RackId == rackId);
        var item = await context.DriftItems.FirstAsync(i => i.DriftReportId == report.Id && i.DriftType == driftType);
        return item.DriftItemId;
    }

    private async Task IngestSnapshotAsync(Guid rackId, int observedVlan, bool includeSecondPort)
    {
        var ports = new List<SwitchPortInfo> { new("ether1", true, observedVlan, Array.Empty<int>()) };
        if (includeSecondPort)
        {
            ports.Add(new("ether2", true, 20, new[] { 20 }));
        }

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

    /// <summary>
    /// Simulates a new Git commit changing the desired access VLAN: desired-state entities are
    /// append-only, so "changing" a value means inserting a whole new, newer
    /// <see cref="DesiredStateVersion"/> tree for the same rack slug — <c>ActiveVersionForRackAsync</c>
    /// then resolves this as the active revision (newest <c>CreatedAtUtc</c>).
    /// </summary>
    private async Task MutateDesiredAccessVlanAsync(string rackSlug, int newVlan)
    {
        await using var context = _postgres.CreateContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main", Guid.NewGuid());
        run.RecordCommit("b".PadLeft(40, '0'), "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        // A deliberately later CreatedAtUtc: ActiveVersionForRackAsync orders by (CreatedAtUtc DESC, Id
        // DESC), and two DesiredStateVersion rows inserted moments apart could otherwise land on the same
        // DateTime.UtcNow tick, leaving the tie-break on the (random) Guid id to non-deterministically pick
        // either version.
        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, "b".PadLeft(40, '0'), run.Id, DateTime.UtcNow.AddMinutes(1),
            "hash-" + Guid.NewGuid().ToString("N"), "{}", 1, "desired-state-ingestion");
        var rackIntent = new DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, "rack-key");
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rackIntent.Id, "sw1", "switch-key");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "ether1", "port-key", accessVlan: newVlan);

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
            Microsoft.Extensions.Options.Options.Create(new DriftComputationOptions()),
            NullLogger<DriftComputationService>.Instance);
        await service.ComputeAndPersistAsync(rackId, Guid.NewGuid());
    }

    /// <summary>An isolated API host bound to the shared harness Postgres, with drift-apply write test doubles wired in.</summary>
    private sealed class ApiHost : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly bool _driftApplyRunnerEnabled;

        public ApiHost(string connectionString, bool driftApplyRunnerEnabled)
        {
            _connectionString = connectionString;
            _driftApplyRunnerEnabled = driftApplyRunnerEnabled;
        }

        public TestSwitchMutatingDriverFactory MutatingDriverFactory { get; } = new();

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

                services.RemoveAll(typeof(IRackDefinitionProvider));
                services.AddScoped<IRackDefinitionProvider, TestRackDefinitionProvider>();
                services.AddSingleton<ISwitchMutatingDriverFactory>(MutatingDriverFactory);

                services.Configure<DiscoveryOrchestrationOptions>(options =>
                {
                    options.RunnerEnabled = false;
                    options.SchedulerEnabled = false;
                });
                services.Configure<DriftApplyOrchestrationOptions>(options =>
                {
                    options.RunnerEnabled = _driftApplyRunnerEnabled;
                    options.RunnerPollSeconds = 1;
                    options.RetryBaseDelayMs = 0;
                    options.HeartbeatStalenessSeconds = 5;
                });

                services.RemoveAll(typeof(Caisson.Ingestion.Git.ReadOnly.IGitRepositoryProvider));
                services.AddSingleton<Caisson.Ingestion.Git.ReadOnly.IGitRepositoryProvider, StubGitRepositoryProvider>();
                services.RemoveAll(typeof(Caisson.Ingestion.Security.IGitIngestionSecretsResolver));
                services.AddSingleton<Caisson.Ingestion.Security.IGitIngestionSecretsResolver, FixedGitIngestionSecretsResolver>();
                services.Configure<Caisson.Ingestion.Options.GitIngestionOptions>(options =>
                {
                    options.RepoUrl = "https://example.com/stub-repo.git";
                });
            });
        }
    }
}
