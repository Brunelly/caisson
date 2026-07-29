using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// Postgres-backed tests of <see cref="Caisson.Orchestration.Drift.DriftRetentionPruner"/> (story #64,
/// NFR5): the hybrid policy keeps a report only if it is BOTH among a rack's newest
/// <c>RetentionMaxReportsPerRack</c> reports AND no older than <c>RetentionMaxDays</c>.
/// </summary>
public sealed class DriftRetentionPrunerTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftRetentionPrunerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Prunes_reports_beyond_the_count_cap_keeping_the_newest()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await SeedReportAsync(rackId, now.AddMinutes(i)));
        }

        var pruner = CreatePruner(maxPerRack: 3, maxDays: 3650);
        await pruner.TickAsync(default);

        await using var verify = _fixture.CreateContext();
        var remaining = await verify.DriftReports.Where(r => r.RackId == rackId).Select(r => r.Id).ToListAsync();

        remaining.Should().BeEquivalentTo(new[] { ids[2], ids[3], ids[4] }); // the 3 newest
    }

    [Fact]
    public async Task Prunes_reports_older_than_the_day_cap_even_within_the_count_limit()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);

        var oldId = await SeedReportAsync(rackId, now.AddDays(-200));
        var newId = await SeedReportAsync(rackId, now.AddDays(-1));

        var pruner = CreatePruner(maxPerRack: 200, maxDays: 180, nowUtc: now);
        await pruner.TickAsync(default);

        await using var verify = _fixture.CreateContext();
        var remaining = await verify.DriftReports.Where(r => r.RackId == rackId).Select(r => r.Id).ToListAsync();

        remaining.Should().BeEquivalentTo(new[] { newId });
        (await verify.DriftReports.AnyAsync(r => r.Id == oldId)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_report_cascades_its_items()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
        var reportId = await SeedReportAsync(rackId, now.AddDays(-200));

        await using (var context = _fixture.CreateContext())
        {
            context.DriftItems.Add(new DriftItem(
                Guid.NewGuid(), reportId, Guid.NewGuid(), rackId, DriftType.AccessVlanMismatch,
                DriftSeverity.High, true, DriftSubjectType.SwitchPort, "v1|r|sw|p", "10", "20", "why", now));
            await context.SaveChangesAsync();
        }

        var pruner = CreatePruner(maxPerRack: 200, maxDays: 180, nowUtc: now);
        await pruner.TickAsync(default);

        await using var verify = _fixture.CreateContext();
        (await verify.DriftReports.AnyAsync(r => r.Id == reportId)).Should().BeFalse();
        (await verify.DriftItems.AnyAsync(i => i.DriftReportId == reportId)).Should().BeFalse();
    }

    private Caisson.Orchestration.Drift.DriftRetentionPruner CreatePruner(int maxPerRack, int maxDays, DateTime? nowUtc = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        var provider = services.BuildServiceProvider();

        var time = nowUtc is { } n ? new FixedTimeProvider(n) : TimeProvider.System;
        return new Caisson.Orchestration.Drift.DriftRetentionPruner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            MsOptions.Create(new DriftOrchestrationOptions { RetentionMaxReportsPerRack = maxPerRack, RetentionMaxDays = maxDays }),
            NullLogger<Caisson.Orchestration.Drift.DriftRetentionPruner>.Instance);
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task<Guid> SeedReportAsync(Guid rackId, DateTime computedAtUtc)
    {
        await using var context = _fixture.CreateContext();

        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), "rack-" + rackId.ToString("N"), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"), "{}", 1, "desired-state-ingestion");
        context.DesiredStateVersions.Add(version);

        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed,
            version: Random.Shared.Next(1, int.MaxValue));
        context.Snapshots.Add(snapshot);

        var report = new DriftReport(
            Guid.NewGuid(), rackId, version.Id, snapshot.Id, computedAtUtc,
            DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson: "{}",
            hasAmbiguities: false, isTruncated: false, DriftComputationStatus.Succeeded);
        context.DriftReports.Add(report);
        await context.SaveChangesAsync();
        return report.Id;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
