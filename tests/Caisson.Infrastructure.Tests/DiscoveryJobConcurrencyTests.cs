using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Runner;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the discovery job invariants (story #8, NFR5): the partial-unique index
/// enforces one active job per rack, idempotency-key replay is distinct, transitions persist, and a
/// stalled job is reclaimable.
/// </summary>
public sealed class DiscoveryJobConcurrencyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DiscoveryJobConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_concurrent_enqueues_for_one_rack_yield_one_created_and_one_conflict()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var resultA = Service(contextA).EnqueueAsync(
            rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), null, false, default);
        var resultB = Service(contextB).EnqueueAsync(
            rackId, TriggerType.OnDemand, "b", ActorType.User, Guid.NewGuid(), null, false, default);
        var results = await Task.WhenAll(resultA, resultB);

        results.Count(r => r.Disposition == EnqueueDisposition.Created).Should().Be(1);
        results.Count(r => r.Disposition == EnqueueDisposition.Conflict).Should().Be(1);

        // Both dispositions reference the single active job.
        results.Select(r => r.JobId).Distinct().Should().ContainSingle();

        await using var verify = _fixture.CreateContext();
        (await verify.DiscoveryJobs.CountAsync(j => j.RackId == rackId)).Should().Be(1);
    }

    [Fact]
    public async Task Repeated_idempotency_key_replays_the_same_job()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        EnqueueResult first, second;
        await using (var context = _fixture.CreateContext())
        {
            first = await Service(context).EnqueueAsync(
                rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), "key-1", false, default);
        }

        await using (var context = _fixture.CreateContext())
        {
            second = await Service(context).EnqueueAsync(
                rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), "key-1", false, default);
        }

        first.Disposition.Should().Be(EnqueueDisposition.Created);
        second.Disposition.Should().Be(EnqueueDisposition.IdempotentReplay);
        second.JobId.Should().Be(first.JobId);

        await using var verify = _fixture.CreateContext();
        (await verify.DiscoveryJobs.CountAsync(j => j.RackId == rackId)).Should().Be(1);
    }

    [Fact]
    public async Task Step_heartbeat_and_terminal_transitions_persist()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        Guid jobId;
        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context).EnqueueAsync(
                rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), null, false, default);
            jobId = result.JobId;
        }

        var at = DateTime.UtcNow;
        await using (var context = _fixture.CreateContext())
        {
            var job = await context.DiscoveryJobs.Include(j => j.Steps).FirstAsync(j => j.Id == jobId);
            job.MarkInProgress(at);
            var step = job.Steps.First(s => s.StepName == DiscoveryStepName.SwitchDiscovery);
            step.BeginAttempt(at);
            await context.SaveChangesAsync();

            step.Succeed(at.AddSeconds(1), "{\"discovered\":1}");
            job.Succeed(at.AddSeconds(2));
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var job = await context.DiscoveryJobs.Include(j => j.Steps).FirstAsync(j => j.Id == jobId);
            job.Status.Should().Be(DiscoveryJobStatus.Succeeded);
            job.LastHeartbeatAtUtc.Should().NotBeNull();
            job.Steps.First(s => s.StepName == DiscoveryStepName.SwitchDiscovery)
                .Status.Should().Be(DiscoveryStepStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Stalled_in_progress_job_is_reclaimed_but_a_fresh_one_is_not()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync(); // the runner claim is global; isolate from sibling tests' jobs
        var rackId = await SeedRackAsync();
        var now = DateTime.UtcNow;

        var stalledId = await InsertInProgressJobAsync(rackId, heartbeatAt: now.AddMinutes(-5));

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DiscoveryJobRunner.ClaimNextAsync(
                context, now, now.AddSeconds(-45), maxAttempts: 5, default);
            claimed.Should().Be(stalledId);
        }

        // With the stalled job now freshly heartbeated, a second claim finds nothing.
        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DiscoveryJobRunner.ClaimNextAsync(
                context, now, now.AddSeconds(-45), maxAttempts: 5, default);
            claimed.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_stalled_job_at_or_over_max_attempts_is_not_reclaimed_and_is_failed_instead()
    {
        // Finding #12: a job that keeps reclaiming and crashing must eventually stop being reclaimed and
        // reach a terminal state instead of retrying forever.
        await _fixture.MigrateAsync();
        await ClearJobsAsync();
        var rackId = await SeedRackAsync();
        var now = DateTime.UtcNow;

        var exhaustedId = await InsertInProgressJobAsync(rackId, heartbeatAt: now.AddMinutes(-5), attemptCount: 5);

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DiscoveryJobRunner.ClaimNextAsync(
                context, now, now.AddSeconds(-45), maxAttempts: 5, default);
            claimed.Should().BeNull("a job at the attempt cap must not be reclaimed");
        }

        await using (var context = _fixture.CreateContext())
        {
            await DiscoveryJobRunner.FailExhaustedStaleJobsAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, default);
        }

        await using var verify = _fixture.CreateContext();
        var job = await verify.DiscoveryJobs.FirstAsync(j => j.Id == exhaustedId);
        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        job.ErrorCode.Should().Be(DiscoveryErrorCodes.MaxAttemptsExceeded);
    }

    [Fact]
    public async Task Queued_job_is_claimed()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync(); // the runner claim is global; isolate from sibling tests' jobs
        var rackId = await SeedRackAsync();

        Guid jobId;
        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context).EnqueueAsync(
                rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), null, false, default);
            jobId = result.JobId;
        }

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DiscoveryJobRunner.ClaimNextAsync(
                context, DateTime.UtcNow, DateTime.UtcNow.AddSeconds(-45), maxAttempts: 5, default);
            claimed.Should().Be(jobId);
        }
    }

    [Fact]
    public async Task Status_last_success_reflects_a_later_on_demand_run_over_an_older_scheduled_one()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        var scheduledSuccess = DateTime.UtcNow.AddHours(-2);
        var onDemandSuccess = DateTime.UtcNow.AddMinutes(-5);

        // A schedule row whose LastSuccessAtUtc is the *older* scheduled success, plus a *later* on-demand
        // succeeded job — the exact case where reading schedule.LastSuccessAtUtc would understate AC4.
        await using (var context = _fixture.CreateContext())
        {
            var schedule = new RackDiscoverySchedule(rackId, enabled: true, intervalSeconds: 3600, jitterSeconds: 0);
            schedule.RecordSuccess(scheduledSuccess);
            context.RackDiscoverySchedules.Add(schedule);
            await context.SaveChangesAsync();
        }

        await InsertSucceededJobAsync(rackId, TriggerType.OnDemand, onDemandSuccess);

        await using (var context = _fixture.CreateContext())
        {
            var status = await Service(context).GetStatusAsync(rackId, default);

            status.LastSuccessAtUtc.Should().BeCloseTo(onDemandSuccess, TimeSpan.FromSeconds(1));
            status.ScheduleEnabled.Should().BeTrue();
        }
    }

    private async Task<Guid> InsertSucceededJobAsync(Guid rackId, TriggerType mode, DateTime finishedAtUtc)
    {
        await using var context = _fixture.CreateContext();
        var job = new DiscoveryJob(
            Guid.NewGuid(), rackId, mode, "a", ActorType.User, Guid.NewGuid(), finishedAtUtc.AddMinutes(-1));
        job.SeedSteps(Guid.NewGuid);
        job.MarkInProgress(finishedAtUtc.AddMinutes(-1));
        job.Succeed(finishedAtUtc);
        context.DiscoveryJobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> InsertInProgressJobAsync(Guid rackId, DateTime heartbeatAt, int attemptCount = 1)
    {
        await using var context = _fixture.CreateContext();
        var job = new DiscoveryJob(
            Guid.NewGuid(), rackId, TriggerType.OnDemand, "a", ActorType.User, Guid.NewGuid(), heartbeatAt);
        job.SeedSteps(Guid.NewGuid);
        for (var i = 0; i < attemptCount; i++)
        {
            job.MarkInProgress(heartbeatAt);
        }

        context.DiscoveryJobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private static DiscoveryJobService Service(CaissonDbContext context)
        => new(
            context,
            new GuidTopologyIdGenerator(),
            TimeProvider.System,
            new DiscoveryJobSignal(),
            new DiscoveryCancellationRegistry(),
            new NoOpTopologyEventPublisher(),
            new InProcessTopologyEventSequencer(),
            new MandatoryAuditOutbox(),
            NullLogger<DiscoveryJobService>.Instance);

    private async Task ClearJobsAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM discovery_job;");
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }
}
