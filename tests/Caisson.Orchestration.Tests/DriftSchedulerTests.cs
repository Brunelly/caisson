using Caisson.Domain.DesiredState;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Drift;
using Caisson.Orchestration.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// Postgres-backed tests of <see cref="DriftScheduler.TickAsync"/> (story #64, AC4): only racks with
/// BOTH an active desired revision and a latest observed snapshot are enqueued, and a rack lacking either
/// input is skipped without affecting the others — mirroring <c>DiscoverySchedulerTests</c>'s pattern,
/// extended to a real database since <c>LatestVersionPerRackAsync</c> requires <c>FromSqlRaw</c>.
/// </summary>
public sealed class DriftSchedulerTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftSchedulerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tick_enqueues_only_racks_with_both_an_active_desired_revision_and_a_snapshot()
    {
        await _fixture.MigrateAsync();

        var eligibleRackId = await SeedRackAsync("rack-eligible");
        await SeedDesiredVersionAsync("rack-eligible");
        await SeedSnapshotAsync(eligibleRackId);

        // Desired revision but no observed snapshot at all.
        await SeedRackAsync("rack-desired-only");
        await SeedDesiredVersionAsync("rack-desired-only");

        // Observed snapshot but no desired revision — never enumerated (LatestVersionPerRackAsync only
        // returns racks that HAVE ingested a desired revision), so implicitly excluded too.
        var observedOnlyRackId = await SeedRackAsync("rack-observed-only");
        await SeedSnapshotAsync(observedOnlyRackId);

        var signal = new DriftRecomputeSignal();
        var scheduler = CreateScheduler(signal);

        await scheduler.TickAsync(default);

        var enqueued = DrainAll(signal);
        enqueued.Should().ContainSingle().Which.Should().Be(eligibleRackId);
    }

    [Fact]
    public async Task Tick_is_a_noop_when_no_rack_has_an_active_desired_revision()
    {
        await _fixture.MigrateAsync();

        var signal = new DriftRecomputeSignal();
        var scheduler = CreateScheduler(signal);

        var act = async () => await scheduler.TickAsync(default);

        await act.Should().NotThrowAsync();
        DrainAll(signal).Should().BeEmpty();
    }

    private DriftScheduler CreateScheduler(DriftRecomputeSignal signal)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        var provider = services.BuildServiceProvider();

        return new DriftScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            TimeProvider.System,
            MsOptions.Create(new DriftOrchestrationOptions()),
            NullLogger<DriftScheduler>.Instance);
    }

    private static List<Guid> DrainAll(DriftRecomputeSignal signal)
    {
        var results = new List<Guid>();
        while (signal.Reader.TryRead(out var rackId))
        {
            results.Add(rackId);
        }

        return results;
    }

    private async Task<Guid> SeedRackAsync(string externalKey)
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, externalKey, "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedDesiredVersionAsync(string rackSlug)
    {
        await using var context = _fixture.CreateContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        var commitSha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        context.DesiredStateVersions.Add(new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, commitSha, run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"),
            "{}", 1, "desired-state-ingestion"));
        await context.SaveChangesAsync();
    }

    private async Task SeedSnapshotAsync(Guid rackId)
    {
        await using var context = _fixture.CreateContext();
        context.Snapshots.Add(new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed));
        await context.SaveChangesAsync();
    }
}
