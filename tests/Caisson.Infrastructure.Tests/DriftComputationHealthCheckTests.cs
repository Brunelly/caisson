using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.HealthChecks;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed test of <see cref="DriftComputationHealthCheck"/> (story #64) on an EMPTY database —
/// a separate class (and therefore a separate <see cref="PostgresFixture"/> instance/database) from
/// <see cref="DriftComputationHealthCheckWithReportTests"/>, since the check's last-run query is global
/// across all racks and would otherwise see that class's seeded report.
/// </summary>
public sealed class DriftComputationHealthCheckEmptyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftComputationHealthCheckEmptyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reports_healthy_with_NeverRun_when_no_drift_has_been_computed()
    {
        await _fixture.MigrateAsync();
        await using var context = _fixture.CreateContext();
        var check = new DriftComputationHealthCheck(context);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["lastRunStatus"].Should().Be("NeverRun");
    }
}

/// <summary>
/// Postgres-backed test of <see cref="DriftComputationHealthCheck"/> (story #64): reports last-run status
/// without ever returning <see cref="HealthStatus.Unhealthy"/>, mirroring <c>GitIngestionHealthCheck</c>'s
/// philosophy — a failing/stuck drift computation must not take <c>/health/ready</c> down.
/// </summary>
public sealed class DriftComputationHealthCheckWithReportTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DriftComputationHealthCheckWithReportTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reports_healthy_with_the_last_run_status_when_a_report_exists()
    {
        await _fixture.MigrateAsync();
        var rackId = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext())
        {
            seed.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));

            var run = new DesiredStateIngestionRun(
                Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
                Guid.NewGuid());
            run.RecordCommit("a".PadLeft(40, '0'), "author", DateTime.UtcNow, "message");
            run.Succeed(DateTime.UtcNow);
            seed.DesiredStateIngestionRuns.Add(run);

            var version = new DesiredStateVersion(
                Guid.NewGuid(), "rack-" + rackId.ToString("N"), "a".PadLeft(40, '0'), run.Id, DateTime.UtcNow,
                "hash-1", "{}", 1, "desired-state-ingestion");
            seed.DesiredStateVersions.Add(version);

            var snapshot = new TopologySnapshot(
                Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);
            seed.Snapshots.Add(snapshot);

            seed.DriftReports.Add(new DriftReport(
                Guid.NewGuid(), rackId, version.Id, snapshot.Id, DateTime.UtcNow,
                DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson: "{}",
                hasAmbiguities: false, isTruncated: false, DriftComputationStatus.Succeeded));
            await seed.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext();
        var check = new DriftComputationHealthCheck(context);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["lastRunStatus"].Should().Be("Succeeded");
        result.Data["lastSuccessAtUtc"].Should().NotBe(string.Empty);
    }
}
