using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests of the ingestion service (AC1/AC2, NFR3): atomic persistence, stored-diff =
/// calculator-diff, diff idempotency, monotonic version uniqueness, and forced-failure rollback. Gated
/// on an available Postgres via <see cref="PostgresFixture"/>.
/// </summary>
public sealed class TopologySnapshotIngestionServiceTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTime At = new(2026, 7, 28, 4, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public TopologySnapshotIngestionServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Persists_snapshot_diffs_summary_and_audit_atomically()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var (input, result) = IngestionTestData.Large();

        SnapshotIngestionOutcome outcome;
        await using (var context = _fixture.CreateContext())
        {
            outcome = await Ingest(context, rackId, input, result);
        }

        outcome.Version.Should().Be(1);

        await using (var context = _fixture.CreateContext())
        {
            var snapshot = await context.SnapshotWithGraphAsync(rackId, outcome.SnapshotId);
            snapshot.Should().NotBeNull();
            snapshot!.Version.Should().Be(1);
            snapshot.Switches.Should().HaveCount(2);
            snapshot.Servers.Should().HaveCount(20);
            snapshot.CandidateMappings.Should().HaveCount(80);
            snapshot.ChangeSummary.Should().NotBeNull();

            (await context.EntityDiffs.CountAsync(d => d.SnapshotId == outcome.SnapshotId))
                .Should().Be(outcome.DiffCount).And.BeGreaterThan(0);
            (await context.AuditEvents.CountAsync(
                a => a.SnapshotId == outcome.SnapshotId && a.Action == "discovery.persisted"))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task Stored_diffs_equal_the_pure_calculator_output()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var input = IngestionTestData.Observed();
        var result = IngestionTestData.Correlation();

        SnapshotIngestionOutcome outcome;
        await using (var context = _fixture.CreateContext())
        {
            outcome = await Ingest(context, rackId, input, result);
        }

        var mapped = TopologySnapshotMapper.Map(
            rackId, IngestionTestData.RunContext(), input, result, new IngestionTestData.SequentialIds().NewId).Snapshot;
        var expected = TopologyDiffCalculator
            .Diff(null, mapped, Guid.NewGuid(), At, new IngestionTestData.SequentialIds().NewId)
            .Diffs.Select(Key).ToHashSet();

        await using (var context = _fixture.CreateContext())
        {
            var stored = await context.EntityDiffs
                .Where(d => d.SnapshotId == outcome.SnapshotId)
                .ToListAsync();
            stored.Select(Key).Should().BeEquivalentTo(expected);
            stored.Should().OnlyContain(d => d.ChangeType == ChangeType.Added);
        }
    }

    [Fact]
    public async Task Reingesting_identical_input_produces_no_new_diffs()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var input = IngestionTestData.Observed();
        var result = IngestionTestData.Correlation();

        SnapshotIngestionOutcome first, second;
        await using (var context = _fixture.CreateContext())
        {
            first = await Ingest(context, rackId, input, result);
        }

        await using (var context = _fixture.CreateContext())
        {
            second = await Ingest(context, rackId, input, result);
        }

        second.Version.Should().Be(2);
        second.DiffCount.Should().Be(0); // identical to v1 → no diffs

        await using (var context = _fixture.CreateContext())
        {
            (await context.EntityDiffs.CountAsync(d => d.SnapshotId == second.SnapshotId)).Should().Be(0);
            // No duplicate diffs overall: every (snapshot, type, key) is unique (backed by the index).
            var all = await context.EntityDiffs.Where(d => d.RackId == rackId).ToListAsync();
            all.Select(d => (d.SnapshotId, d.EntityType, d.EntityStableKey)).Should().OnlyHaveUniqueItems();
            _ = first;
        }
    }

    [Fact]
    public async Task Version_is_monotonic_and_the_unique_index_rejects_a_duplicate()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var input = IngestionTestData.Observed();
        var result = IngestionTestData.Correlation();

        await using (var context = _fixture.CreateContext())
        {
            (await Ingest(context, rackId, input, result)).Version.Should().Be(1);
        }

        await using (var context = _fixture.CreateContext())
        {
            (await Ingest(context, rackId, input, result)).Version.Should().Be(2);
        }

        await using (var context = _fixture.CreateContext())
        {
            context.Snapshots.Add(new TopologySnapshot(
                Guid.NewGuid(), rackId, At, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed, version: 1));
            var act = async () => await context.SaveChangesAsync();
            var assertion = await act.Should().ThrowAsync<DbUpdateException>();
            assertion.Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task Forced_failure_rolls_back_to_a_complete_or_nothing_state()
    {
        await _fixture.MigrateAsync();
        var missingRackId = Guid.NewGuid(); // no Rack row → snapshot FK violation mid-persist
        var input = IngestionTestData.Observed();
        var result = IngestionTestData.Correlation();

        await using (var context = _fixture.CreateContext())
        {
            var act = async () => await Ingest(context, missingRackId, input, result);
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var context = _fixture.CreateContext())
        {
            (await context.Snapshots.CountAsync(s => s.RackId == missingRackId)).Should().Be(0);
            (await context.EntityDiffs.CountAsync(d => d.RackId == missingRackId)).Should().Be(0);
            (await context.AuditEvents.CountAsync(a => a.RackId == missingRackId)).Should().Be(0);
        }
    }

    private static Task<SnapshotIngestionOutcome> Ingest(
        CaissonDbContext context, Guid rackId, TopologyCorrelationInput input, TopologyCorrelationResult result)
    {
        var service = new TopologySnapshotIngestionService(context, new GuidTopologyIdGenerator());
        var request = new TopologyIngestionRequest(
            rackId, input, result, TriggerType.OnDemand, "svc-discovery", ActorType.ServiceAccount,
            "chr", "7.15", Guid.NewGuid(), SnapshotStatus.Completed, At, At);
        return service.IngestAsync(request);
    }

    private static string Key(TopologyEntityDiff d) => $"{d.EntityType}|{d.EntityStableKey}|{d.ChangeType}";

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }
}
