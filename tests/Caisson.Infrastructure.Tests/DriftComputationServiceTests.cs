using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Drift;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests of <see cref="DriftComputationService"/> (story #64, AC3): idempotent upsert on
/// identical recompute, a new report on a new revision/snapshot with history preserved, and safe
/// concurrent recompute of the same tuple — mirroring
/// <c>DesiredStateIngestionServiceConcurrencyTests</c>'s pattern of exercising the real service against a
/// real database.
/// </summary>
public sealed class DriftComputationServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftComputationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task First_compute_inserts_a_report_with_items_and_an_audit_event()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-first");
        await SeedDesiredAsync("rack-first", accessVlan: 10);
        var snapshotId = await SeedSnapshotAsync(rackId, pvid: 20); // mismatch -> one AccessVlanMismatch item

        var correlationId = Guid.NewGuid();
        await using (var context = _fixture.CreateContext())
        {
            await Service(context).ComputeAndPersistAsync(rackId, correlationId);
        }

        await using var verify = _fixture.CreateContext();
        var report = await verify.LatestReportForRackAsync(rackId);
        report.Should().NotBeNull();
        report!.ObservedSnapshotId.Should().Be(snapshotId);
        report.Status.Should().Be(DriftComputationStatus.Succeeded);
        report.TotalItems.Should().Be(1);

        (await verify.DriftItems.CountAsync(i => i.DriftReportId == report.Id)).Should().Be(1);
        (await verify.AuditEvents.CountAsync(a => a.Action == "drift.report.computed" && a.RackId == rackId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Recompute_of_the_identical_tuple_updates_the_report_in_place()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-recompute");
        await SeedDesiredAsync("rack-recompute", accessVlan: 10);
        await SeedSnapshotAsync(rackId, pvid: 20);

        Guid firstReportId;
        DateTime firstComputedAt;
        await using (var context = _fixture.CreateContext())
        {
            await Service(context).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        }

        await using (var read1 = _fixture.CreateContext())
        {
            var report = await read1.LatestReportForRackAsync(rackId);
            firstReportId = report!.Id;
            firstComputedAt = report.ComputedAtUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await using (var context = _fixture.CreateContext())
        {
            await Service(context).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        }

        await using var verify = _fixture.CreateContext();
        (await verify.DriftReports.CountAsync(r => r.RackId == rackId)).Should().Be(1);
        var report2 = await verify.LatestReportForRackAsync(rackId);
        report2!.Id.Should().Be(firstReportId);
        report2.ComputedAtUtc.Should().BeAfter(firstComputedAt);
        (await verify.DriftItems.CountAsync(i => i.DriftReportId == firstReportId)).Should().Be(1);
    }

    [Fact]
    public async Task New_observed_snapshot_produces_a_new_report_and_preserves_history()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-history");
        await SeedDesiredAsync("rack-history", accessVlan: 10);
        var firstSnapshotId = await SeedSnapshotAsync(rackId, pvid: 20, version: 1);

        await using (var context = _fixture.CreateContext())
        {
            await Service(context).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        }

        var secondSnapshotId = await SeedSnapshotAsync(rackId, pvid: 30, version: 2);
        await using (var context = _fixture.CreateContext())
        {
            await Service(context).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        }

        await using var verify = _fixture.CreateContext();
        (await verify.DriftReports.CountAsync(r => r.RackId == rackId)).Should().Be(2);

        var latest = await verify.LatestReportForRackAsync(rackId);
        latest!.ObservedSnapshotId.Should().Be(secondSnapshotId);

        var history = await verify.ReportHistoryPageAsync(rackId, after: null, limit: 10);
        history.Select(r => r.ObservedSnapshotId).Should().Contain(new[] { firstSnapshotId, secondSnapshotId });
    }

    [Fact]
    public async Task Concurrent_recompute_of_the_same_tuple_converges_to_one_report()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-race");
        await SeedDesiredAsync("rack-race", accessVlan: 10);
        await SeedSnapshotAsync(rackId, pvid: 20);

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var taskA = Service(contextA).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        var taskB = Service(contextB).ComputeAndPersistAsync(rackId, Guid.NewGuid());
        await Task.WhenAll(taskA, taskB);

        await using var verify = _fixture.CreateContext();
        (await verify.DriftReports.CountAsync(r => r.RackId == rackId)).Should().Be(1);
        var report = await verify.LatestReportForRackAsync(rackId);
        report!.Status.Should().Be(DriftComputationStatus.Succeeded);
        (await verify.DriftItems.CountAsync(i => i.DriftReportId == report.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Compute_for_an_unknown_rack_is_a_noop()
    {
        await _fixture.MigrateAsync();
        var unknownRackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();

        var act = async () => await Service(context).ComputeAndPersistAsync(unknownRackId, Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await context.DriftReports.CountAsync(r => r.RackId == unknownRackId)).Should().Be(0);
    }

    [Fact]
    public async Task Compute_with_no_desired_revision_or_no_observed_snapshot_is_a_noop()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-incomplete");

        await using var context = _fixture.CreateContext();
        await Service(context).ComputeAndPersistAsync(rackId, Guid.NewGuid());

        (await context.DriftReports.CountAsync(r => r.RackId == rackId)).Should().Be(0);
    }

    [Fact]
    public async Task Successful_compute_logs_rack_desired_revision_snapshot_report_and_correlation_ids()
    {
        // NFR4: every drift computation must emit structured logs carrying rackId, desiredRevisionId,
        // observedSnapshotId, driftReportId and correlationId — verified here via a capturing test sink.
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync("rack-logging");
        await SeedDesiredAsync("rack-logging", accessVlan: 10);
        await SeedSnapshotAsync(rackId, pvid: 20);

        var logger = new CapturingLogger<DriftComputationService>();
        var correlationId = Guid.NewGuid();

        Guid driftReportId;
        Guid desiredRevisionId;
        Guid observedSnapshotId;
        await using (var context = _fixture.CreateContext())
        {
            await Service(context, logger).ComputeAndPersistAsync(rackId, correlationId);
        }

        await using (var verify = _fixture.CreateContext())
        {
            var report = await verify.LatestReportForRackAsync(rackId);
            driftReportId = report!.Id;
            desiredRevisionId = report.DesiredRevisionId;
            observedSnapshotId = report.ObservedSnapshotId;
        }

        var successLine = logger.Messages.Should().ContainSingle(m => m.StartsWith("Drift computed", StringComparison.Ordinal)).Subject;
        successLine.Should().Contain(rackId.ToString());
        successLine.Should().Contain(desiredRevisionId.ToString());
        successLine.Should().Contain(observedSnapshotId.ToString());
        successLine.Should().Contain(driftReportId.ToString());
        successLine.Should().Contain(correlationId.ToString());
    }

    private static DriftComputationService Service(CaissonDbContext context, Microsoft.Extensions.Logging.ILogger<DriftComputationService>? logger = null)
        => new(
            context, new GuidTopologyIdGenerator(), TimeProvider.System,
            Options.Create(new DriftComputationOptions()), logger ?? NullLogger<DriftComputationService>.Instance);

    private async Task<Guid> SeedRackAsync(string externalKey)
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, externalKey, "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedDesiredAsync(string rackSlug, int accessVlan)
    {
        await using var context = _fixture.CreateContext();
        var commitSha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // unique per call — the fixture's DB is shared across this class's tests
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, commitSha, run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"),
            "{}", 1, "desired-state-ingestion");
        var rackIntent = new DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, "rack-key");
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rackIntent.Id, "sw1", "switch-key");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "ether1", "port-key", accessVlan);

        context.DesiredStateVersions.Add(version);
        context.DesiredRackIntents.Add(rackIntent);
        context.DesiredSwitchIntents.Add(switchIntent);
        context.DesiredPortIntents.Add(port);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedSnapshotAsync(Guid rackId, int pvid, int version = 1)
    {
        await using var context = _fixture.CreateContext();
        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed,
            version: version);
        var sw = new Switch(Guid.NewGuid(), rackId, snapshot.Id, DateTime.UtcNow, "sw1");
        var port = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshot.Id, "ether1", isUp: true, pvid: pvid);
        sw.AddPort(port);
        snapshot.AddSwitch(sw);

        context.Snapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return snapshot.Id;
    }
}
