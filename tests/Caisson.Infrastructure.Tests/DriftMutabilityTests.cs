using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// The negative counterpart to <see cref="ImmutabilityTests"/> (ADR 0028): proves
/// <see cref="DriftReport"/>/<see cref="DriftItem"/> are correctly EXCLUDED from
/// <c>CaissonDbContext.GuardAppendOnly()</c> — neither implements <c>IAppendOnly</c>/
/// <c>ISnapshotScoped</c>, so update and delete must succeed, unlike every append-only observed-state
/// entity.
/// </summary>
public sealed class DriftMutabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftMutabilityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Mutating_a_persisted_drift_report_is_allowed()
    {
        await _fixture.MigrateAsync();
        var reportId = await SeedReportAsync();

        await using var context = _fixture.CreateContext();
        var report = await context.DriftReports.SingleAsync(r => r.Id == reportId);

        report.RecordRecomputation(DateTime.UtcNow, DriftSchema.CurrentComputationVersion, totalItems: 1, "{\"total\":1}", hasAmbiguities: false, isTruncated: false);

        var act = async () => await context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Deleting_a_persisted_drift_report_is_allowed()
    {
        await _fixture.MigrateAsync();
        var reportId = await SeedReportAsync();

        await using var context = _fixture.CreateContext();
        var report = await context.DriftReports.SingleAsync(r => r.Id == reportId);
        context.DriftReports.Remove(report);

        var act = async () => await context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
        (await context.DriftReports.AnyAsync(r => r.Id == reportId)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_drift_report_cascades_its_items_without_a_guard_violation()
    {
        await _fixture.MigrateAsync();
        var reportId = await SeedReportAsync();

        await using (var context = _fixture.CreateContext())
        {
            var rackId = (await context.DriftReports.SingleAsync(r => r.Id == reportId)).RackId;
            context.DriftItems.Add(new DriftItem(
                Guid.NewGuid(), reportId, Guid.NewGuid(), rackId, DriftType.AccessVlanMismatch,
                DriftSeverity.High, actionable: true, DriftSubjectType.SwitchPort, "v1|r|sw|p", "10", "20",
                "why", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var report = await context.DriftReports.SingleAsync(r => r.Id == reportId);
            context.DriftReports.Remove(report);

            var act = async () => await context.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }

        await using var verify = _fixture.CreateContext();
        (await verify.DriftItems.AnyAsync(i => i.DriftReportId == reportId)).Should().BeFalse();
    }

    private async Task<Guid> SeedReportAsync()
    {
        var rackId = Guid.NewGuid();
        var commitSha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));

        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);

        var version = new DesiredStateVersion(
            Guid.NewGuid(), "rack-" + rackId.ToString("N"), commitSha, run.Id, DateTime.UtcNow,
            "hash-" + Guid.NewGuid().ToString("N"), "{}", 1, "desired-state-ingestion");
        context.DesiredStateVersions.Add(version);

        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);
        context.Snapshots.Add(snapshot);

        var report = new DriftReport(
            Guid.NewGuid(), rackId, version.Id, snapshot.Id, DateTime.UtcNow,
            DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson: "{}",
            hasAmbiguities: false, isTruncated: false, DriftComputationStatus.Succeeded);
        context.DriftReports.Add(report);
        await context.SaveChangesAsync();
        return report.Id;
    }
}
