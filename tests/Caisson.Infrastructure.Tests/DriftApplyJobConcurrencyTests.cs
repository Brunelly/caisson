using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.Runner;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the drift-apply job invariants (story #65, AC4/AC5/NFR2/NFR3): the
/// partial-unique index enforces one active job per drift item, the atomic claim single-winners a race,
/// a terminal job is never re-claimable, and a stalled job is reclaimed then eventually failed at the
/// attempt cap. Mirrors <see cref="DiscoveryJobConcurrencyTests"/>'s shape.
/// </summary>
public sealed class DriftApplyJobConcurrencyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftApplyJobConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_concurrent_request_applies_for_one_drift_item_yield_one_created_and_one_existing()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var item = NewItem(rackId);

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var resultA = Service(contextA).RequestApplyAsync(item, "a", ActorType.User, Guid.NewGuid(), default);
        var resultB = Service(contextB).RequestApplyAsync(item, "b", ActorType.User, Guid.NewGuid(), default);
        var results = await Task.WhenAll(resultA, resultB);

        results.Count(r => r.Disposition == RequestApplyDisposition.Created).Should().Be(1);
        results.Count(r => r.Disposition == RequestApplyDisposition.ExistingActiveJob).Should().Be(1);
        results.Select(r => r.JobId).Distinct().Should().ContainSingle();

        await using var verify = _fixture.CreateContext();
        (await verify.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == item.DriftItemId)).Should().Be(1);
    }

    [Fact]
    public async Task A_new_request_apply_after_the_first_job_terminates_creates_a_second_job()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var item = NewItem(rackId);

        Guid firstJobId;
        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context).RequestApplyAsync(item, "a", ActorType.User, Guid.NewGuid(), default);
            firstJobId = result.JobId;
        }

        await using (var context = _fixture.CreateContext())
        {
            var job = await context.DriftApplyJobs.FirstAsync(j => j.Id == firstJobId);
            job.Complete(DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context).RequestApplyAsync(item, "a", ActorType.User, Guid.NewGuid(), default);
            result.Disposition.Should().Be(RequestApplyDisposition.Created);
            result.JobId.Should().NotBe(firstJobId);
        }

        await using var verify = _fixture.CreateContext();
        (await verify.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == item.DriftItemId)).Should().Be(2);
    }

    [Fact]
    public async Task Queued_job_is_claimed_exactly_once_across_a_race()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync();
        var rackId = await SeedRackAsync();
        var item = NewItem(rackId);

        Guid jobId;
        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context).RequestApplyAsync(item, "a", ActorType.User, Guid.NewGuid(), default);
            jobId = result.JobId;
        }

        var now = DateTime.UtcNow;
        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var claimA = DriftApplyJobRunner.ClaimNextAsync(contextA, now, now.AddSeconds(-45), maxAttempts: 5, "instance-a", default);
        var claimB = DriftApplyJobRunner.ClaimNextAsync(contextB, now, now.AddSeconds(-45), maxAttempts: 5, "instance-b", default);
        var claimed = await Task.WhenAll(claimA, claimB);

        claimed.Count(c => c == jobId).Should().Be(1);
        claimed.Count(c => c is null).Should().Be(1);

        await using var verify = _fixture.CreateContext();
        var job = await verify.DriftApplyJobs.FirstAsync(j => j.Id == jobId);
        job.Status.Should().Be(DriftApplyJobStatus.Claimed);
        job.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task A_terminal_job_is_never_reclaimed()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync();
        var rackId = await SeedRackAsync();
        var jobId = await InsertJobAsync(rackId, DriftApplyJobStatus.Completed, heartbeatAt: DateTime.UtcNow.AddMinutes(-10));

        await using var context = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var claimed = await DriftApplyJobRunner.ClaimNextAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, "instance-a", default);

        claimed.Should().NotBe(jobId);
    }

    [Fact]
    public async Task A_stalled_job_is_reclaimed_then_a_fresh_claim_finds_nothing()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync();
        var rackId = await SeedRackAsync();
        var now = DateTime.UtcNow;
        var stalledId = await InsertJobAsync(rackId, DriftApplyJobStatus.Executing, heartbeatAt: now.AddMinutes(-5));

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DriftApplyJobRunner.ClaimNextAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, "instance-a", default);
            claimed.Should().Be(stalledId);
        }

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DriftApplyJobRunner.ClaimNextAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, "instance-b", default);
            claimed.Should().BeNull("the reclaim just refreshed its heartbeat");
        }
    }

    [Fact]
    public async Task A_stalled_job_at_the_attempt_cap_is_not_reclaimed_and_is_failed_instead()
    {
        await _fixture.MigrateAsync();
        await ClearJobsAsync();
        var rackId = await SeedRackAsync();
        var now = DateTime.UtcNow;
        var exhaustedId = await InsertJobAsync(rackId, DriftApplyJobStatus.Executing, heartbeatAt: now.AddMinutes(-5), attemptCount: 5);

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await DriftApplyJobRunner.ClaimNextAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, "instance-a", default);
            claimed.Should().BeNull("a job at the attempt cap must not be reclaimed");
        }

        await using (var context = _fixture.CreateContext())
        {
            await DriftApplyJobRunner.FailExhaustedStaleJobsAsync(context, now, now.AddSeconds(-45), maxAttempts: 5, default);
        }

        await using var verify = _fixture.CreateContext();
        var job = await verify.DriftApplyJobs.FirstAsync(j => j.Id == exhaustedId);
        job.Status.Should().Be(DriftApplyJobStatus.Failed);
        job.ErrorCode.Should().Be(DriftApplyErrorCodes.MaxAttemptsExceeded);
    }

    private static DriftApplyJobService Service(CaissonDbContext context)
        => new(
            context,
            new GuidTopologyIdGenerator(),
            TimeProvider.System,
            new DriftApplyJobSignal(),
            new NoOpTopologyEventPublisher(),
            new InProcessTopologyEventSequencer(),
            NullLogger<DriftApplyJobService>.Instance);

    private async Task ClearJobsAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM drift_apply_job;");
    }

    private async Task<Guid> InsertJobAsync(
        Guid rackId, DriftApplyJobStatus status, DateTime heartbeatAt, int attemptCount = 1)
    {
        await using var context = _fixture.CreateContext();
        var job = new DriftApplyJob(
            Guid.NewGuid(), rackId, Guid.NewGuid(), "a", ActorType.User, Guid.NewGuid(), heartbeatAt,
            Guid.NewGuid(), 10, 20);
        job.SeedSteps(Guid.NewGuid);

        for (var i = 0; i < attemptCount; i++)
        {
            job.MarkClaimed("instance-seed", heartbeatAt);
        }

        switch (status)
        {
            case DriftApplyJobStatus.Executing:
                job.MarkExecuting(heartbeatAt);
                break;
            case DriftApplyJobStatus.Completed:
                job.Complete(heartbeatAt);
                break;
        }

        context.DriftApplyJobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private static DriftItem NewItem(Guid rackId)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), rackId, DriftType.AccessVlanMismatch,
            DriftSeverity.High, actionable: true, DriftSubjectType.SwitchPort, "v1|rack|sw1|ether1",
            "20", "10", "why", DateTime.UtcNow, "{\"switchName\":\"sw1\",\"portName\":\"ether1\"}");
}
