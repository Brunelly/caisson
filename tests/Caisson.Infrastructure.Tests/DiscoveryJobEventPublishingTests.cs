using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.Runner;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed proof that the discovery-job status transitions publish live events (story #9, AC1):
/// enqueue → Queued, claim → InProgress, and each terminal path, with a monotonic per-job seq and the
/// correct error fields — and that a throwing publisher never aborts the job (AC4/NFR3). The runner is
/// driven with a fake orchestrator so no drivers are needed; the enqueue seam and the real runner are
/// exercised end-to-end against a real database.
/// </summary>
public sealed class DiscoveryJobEventPublishingTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DiscoveryJobEventPublishingTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Enqueue_claim_and_success_publish_status_events_with_monotonic_seq()
    {
        await _fixture.MigrateAsync();
        var publisher = new RecordingTopologyEventPublisher();
        await using var provider = BuildProvider(publisher, TerminalOutcome.Succeed);
        var rackId = await SeedRackAsync(provider);

        var (jobId, correlationId) = await RunOneJobAsync(provider, rackId);

        var statuses = publisher.Statuses.Where(s => s.JobId == jobId).ToList();
        statuses.Select(s => s.Status).Should().ContainInOrder("Queued", "InProgress", "Succeeded");
        statuses.Should().OnlyContain(s => s.RackId == rackId && s.CorrelationId == correlationId);

        // Monotonic per-job seq across the transitions (Redis INCR in production; in-process here).
        var seqs = statuses.Select(s => s.Seq).ToList();
        seqs.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        var succeeded = statuses.Single(s => s.Status == "Succeeded");
        succeeded.PreviousStatus.Should().Be("InProgress");
        succeeded.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Failed_job_publishes_failed_status_with_error_code()
    {
        await _fixture.MigrateAsync();
        var publisher = new RecordingTopologyEventPublisher();
        await using var provider = BuildProvider(publisher, TerminalOutcome.Fail);
        var rackId = await SeedRackAsync(provider);

        var (jobId, _) = await RunOneJobAsync(provider, rackId);

        var failed = publisher.Statuses.Single(s => s.JobId == jobId && s.Status == "Failed");
        failed.ErrorCode.Should().Be(FakeOrchestrator.FailureCode);
    }

    [Fact]
    public async Task A_throwing_publisher_never_aborts_the_job()
    {
        await _fixture.MigrateAsync();
        await using var provider = BuildProvider(new ThrowingTopologyEventPublisher(), TerminalOutcome.Succeed);
        var rackId = await SeedRackAsync(provider);

        var (jobId, _) = await RunOneJobAsync(provider, rackId);

        await using var context = provider.CreateScope().ServiceProvider.GetRequiredService<CaissonDbContext>();
        var status = await context.DiscoveryJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync();
        status.Should().Be(DiscoveryJobStatus.Succeeded);
    }

    private async Task<(Guid JobId, Guid CorrelationId)> RunOneJobAsync(ServiceProvider provider, Guid rackId)
    {
        var runner = new DiscoveryJobRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<DiscoveryJobSignal>(),
            provider.GetRequiredService<DiscoveryCancellationRegistry>(),
            TimeProvider.System,
            provider.GetRequiredService<ITopologyEventPublisher>(),
            provider.GetRequiredService<ITopologyEventSequencer>(),
            provider.GetRequiredService<IOptions<DiscoveryOrchestrationOptions>>(),
            NullLogger<DiscoveryJobRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await runner.StartAsync(cts.Token);

        var correlationId = Guid.NewGuid();
        Guid jobId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IDiscoveryJobService>();
            var result = await service.EnqueueAsync(
                rackId, TriggerType.OnDemand, "tester", ActorType.ServiceAccount, correlationId,
                idempotencyKey: null, dryRun: false, cts.Token);
            result.Disposition.Should().Be(EnqueueDisposition.Created);
            jobId = result.JobId;
        }

        DiscoveryJobStatus status = DiscoveryJobStatus.Queued;
        for (var i = 0; i < 120 && !IsTerminal(status); i++)
        {
            await Task.Delay(250, cts.Token);
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            status = await context.DiscoveryJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync(cts.Token);
        }

        await runner.StopAsync(CancellationToken.None);
        IsTerminal(status).Should().BeTrue("the runner should drive the job to a terminal state");
        return (jobId, correlationId);
    }

    private static bool IsTerminal(DiscoveryJobStatus status)
        => status is DiscoveryJobStatus.Succeeded or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Canceled;

    private ServiceProvider BuildProvider(ITopologyEventPublisher publisher, TerminalOutcome outcome)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITopologyIdGenerator, GuidTopologyIdGenerator>();
        services.AddSingleton<DiscoveryJobSignal>();
        services.AddSingleton<DiscoveryCancellationRegistry>();
        services.AddSingleton(publisher);
        services.AddSingleton<ITopologyEventSequencer, InProcessTopologyEventSequencer>();
        services.AddSingleton(Options.Create(new DiscoveryOrchestrationOptions
        {
            RunnerEnabled = true,
            SchedulerEnabled = false,
            RunnerPollSeconds = 1,
            HeartbeatStalenessSeconds = 5,
            RetryBaseDelayMs = 0,
        }));
        services.AddScoped<IDiscoveryJobService, DiscoveryJobService>();
        services.AddScoped<IDiscoveryOrchestrator>(_ => new FakeOrchestrator(outcome));
        return services.BuildServiceProvider();
    }

    private async Task<Guid> SeedRackAsync(ServiceProvider provider)
    {
        var rackId = Guid.NewGuid();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Event Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private enum TerminalOutcome
    {
        Succeed,
        Fail,
    }

    /// <summary>A hardware-free orchestrator that drives the claimed job to a scripted terminal state.</summary>
    private sealed class FakeOrchestrator : IDiscoveryOrchestrator
    {
        public const string FailureCode = "SWITCH_DISCOVERY_FAILED";

        private readonly TerminalOutcome _outcome;

        public FakeOrchestrator(TerminalOutcome outcome) => _outcome = outcome;

        public Task RunAsync(DiscoveryJob job, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            if (_outcome == TerminalOutcome.Fail)
            {
                job.Fail(now, FailureCode, "device unreachable");
            }
            else
            {
                job.Succeed(now);
            }

            return Task.CompletedTask;
        }
    }
}
