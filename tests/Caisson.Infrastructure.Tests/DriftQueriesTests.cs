using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests of the bounded/keyset <see cref="DriftQueries"/> helpers (story #64, AC5): no
/// unbounded <c>ToListAsync</c>, rack-scoped lookups (a cross-rack id never leaks), and the composite
/// keyset never drops rows that share a boundary timestamp — mirroring <c>KeysetPaginationTests</c>.
/// </summary>
public sealed class DriftQueriesTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private int _nextSnapshotVersion = 1;

    public DriftQueriesTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LatestReportForRackAsync_returns_the_newest_report_by_computed_at_then_id()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        var older = await SeedReportAsync(rackId, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        var newer = await SeedReportAsync(rackId, new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));

        await using var context = _fixture.CreateContext();
        var latest = await context.LatestReportForRackAsync(rackId);

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(newer);
        latest.Id.Should().NotBe(older);
    }

    [Fact]
    public async Task ReportHistoryPageAsync_returns_every_report_when_computed_at_collides()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        var sharedInstant = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 7; i++)
        {
            expected.Add(await SeedReportAsync(rackId, sharedInstant));
        }

        const int pageSize = 3;
        await using var context = _fixture.CreateContext();
        var seen = new HashSet<Guid>();
        Caisson.Infrastructure.Persistence.Shaping.KeysetPosition? after = null;
        for (var guard = 0; guard < 20; guard++)
        {
            // Mirrors the controller pattern: over-fetch by one to detect whether a further page exists.
            var page = await context.ReportHistoryPageAsync(rackId, after, limit: pageSize + 1);
            if (page.Count == 0)
            {
                break;
            }

            var hasMore = page.Count > pageSize;
            var toKeep = hasMore ? page.Take(pageSize).ToList() : page;
            foreach (var r in toKeep)
            {
                seen.Add(r.Id);
            }

            if (!hasMore)
            {
                break;
            }

            var last = toKeep[^1];
            after = new Caisson.Infrastructure.Persistence.Shaping.KeysetPosition(last.ComputedAtUtc, last.Id);
        }

        seen.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ReportByIdAsync_scoped_to_a_different_rack_returns_null()
    {
        await _fixture.MigrateAsync();
        var rackA = await SeedRackAsync();
        var rackB = await SeedRackAsync();
        var reportId = await SeedReportAsync(rackA, DateTime.UtcNow);

        await using var context = _fixture.CreateContext();
        (await context.ReportByIdAsync(rackA, reportId)).Should().NotBeNull();
        (await context.ReportByIdAsync(rackB, reportId)).Should().BeNull();
    }

    [Fact]
    public async Task ItemsPageAsync_applies_severity_driftType_and_actionable_filters()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var reportId = await SeedReportAsync(rackId, DateTime.UtcNow);

        await using (var context = _fixture.CreateContext())
        {
            context.DriftItems.Add(NewItem(reportId, rackId, DriftType.MissingDesiredEntity, DriftSeverity.High, actionable: true));
            context.DriftItems.Add(NewItem(reportId, rackId, DriftType.ExtraObservedEntity, DriftSeverity.Low, actionable: true));
            context.DriftItems.Add(NewItem(reportId, rackId, DriftType.UnknownTopologyMapping, DriftSeverity.Medium, actionable: false));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();

        (await verify.ItemsPageAsync(reportId, DriftSeverity.High, null, null, null, 10)).Should().ContainSingle();
        (await verify.ItemsPageAsync(reportId, null, DriftType.ExtraObservedEntity, null, null, 10)).Should().ContainSingle();
        (await verify.ItemsPageAsync(reportId, null, null, actionable: false, null, 10)).Should().ContainSingle();
        (await verify.ItemsPageAsync(reportId, null, null, null, null, 10)).Should().HaveCount(3);
    }

    [Fact]
    public async Task ItemByDriftItemIdAsync_resolves_the_latest_report_containing_that_id_and_is_rack_scoped()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var otherRackId = await SeedRackAsync();

        var driftItemId = Guid.NewGuid();
        var olderReportId = await SeedReportAsync(rackId, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        var newerReportId = await SeedReportAsync(rackId, new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));

        await using (var context = _fixture.CreateContext())
        {
            context.DriftItems.Add(NewItem(olderReportId, rackId, DriftType.AccessVlanMismatch, DriftSeverity.High, true, driftItemId));
            context.DriftItems.Add(NewItem(newerReportId, rackId, DriftType.AccessVlanMismatch, DriftSeverity.High, true, driftItemId));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        var resolved = await verify.ItemByDriftItemIdAsync(rackId, driftItemId);
        resolved.Should().NotBeNull();
        resolved!.DriftReportId.Should().Be(newerReportId);

        (await verify.ItemByDriftItemIdAsync(otherRackId, driftItemId)).Should().BeNull();
    }

    private static DriftItem NewItem(
        Guid reportId, Guid rackId, DriftType type, DriftSeverity severity, bool actionable, Guid? driftItemId = null)
        => new(
            Guid.NewGuid(), reportId, driftItemId ?? Guid.NewGuid(), rackId, type, severity, actionable,
            DriftSubjectType.SwitchPort, "v1|rack|sw1|ether1", "10", "20", "why", DateTime.UtcNow);

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

        // DriftReport's DesiredRevisionId/ObservedSnapshotId are real FKs (Restrict) — each report needs
        // its own distinct revision/snapshot pair to satisfy the unique (rack, revision, snapshot) index
        // across repeated calls for the same rack in these tests.
        var run = new Caisson.Domain.DesiredState.DesiredStateIngestionRun(
            Guid.NewGuid(), Caisson.Domain.DesiredState.IngestionTriggerType.Poll, DateTime.UtcNow,
            "https://example.com/repo.git", "main", Guid.NewGuid());
        run.RecordCommit(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new Caisson.Domain.DesiredState.DesiredStateVersion(
            Guid.NewGuid(), "rack-" + rackId.ToString("N"), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"), "{}", 1, "desired-state-ingestion");
        context.DesiredStateVersions.Add(version);

        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed,
            version: System.Threading.Interlocked.Increment(ref _nextSnapshotVersion));
        context.Snapshots.Add(snapshot);

        var report = new DriftReport(
            Guid.NewGuid(), rackId, version.Id, snapshot.Id, computedAtUtc,
            DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson: "{}",
            hasAmbiguities: false, isTruncated: false, DriftComputationStatus.Succeeded);
        context.DriftReports.Add(report);
        await context.SaveChangesAsync();
        return report.Id;
    }
}
