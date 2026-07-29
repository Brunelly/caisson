using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Proves the composite <c>(timestamp, id)</c> keyset pagination never drops rows that share the boundary
/// timestamp — the tie-break is applied in the page predicate, not only in the ordering. Audit events
/// frequently share an <c>OccurredAtUtc</c> (a discovery event plus several API-access reads at the same
/// tick), so a timestamp-only cursor would silently skip audit records at a page boundary.
/// </summary>
public sealed class KeysetPaginationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public KeysetPaginationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Audit_pagination_returns_every_row_when_timestamps_collide()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        // Ten audit events all at the SAME instant — the pathological case for a timestamp-only cursor.
        var sharedInstant = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var expected = new HashSet<Guid>();
        await using (var context = _fixture.CreateContext())
        {
            for (var i = 0; i < 10; i++)
            {
                var id = Guid.NewGuid();
                expected.Add(id);
                context.AuditEvents.Add(new TopologyAuditEvent(
                    id, sharedInstant, ActorType.ServiceAccount, "svc", "audit.read", "rack",
                    Guid.NewGuid(), "success", rackId: rackId, targetId: rackId.ToString()));
            }

            await context.SaveChangesAsync();
        }

        var seen = await PageAllAuditAsync(rackId, pageSize: 3);

        seen.Should().BeEquivalentTo(expected); // no row skipped, none duplicated across page boundaries
    }

    [Fact]
    public async Task Snapshot_history_pagination_returns_every_row_when_created_at_collide()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        // Two snapshots completed at the same tick (created_at == completed_at) — collide on the keyset ts.
        var sharedInstant = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var expected = new HashSet<Guid>();
        await using (var context = _fixture.CreateContext())
        {
            for (var version = 1; version <= 4; version++)
            {
                var snapshot = new TopologySnapshot(
                    Guid.NewGuid(), rackId, sharedInstant, "svc", "chr", Guid.NewGuid(),
                    SnapshotStatus.Completed, version: version);
                expected.Add(snapshot.Id);
                context.Snapshots.Add(snapshot);
            }

            await context.SaveChangesAsync();
        }

        var seen = await PageAllSnapshotHistoryAsync(rackId, pageSize: 1);

        seen.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Entity_history_is_capped_and_pagination_returns_every_row_when_created_at_collide()
    {
        // Finding #4: EntityHistoryAsync previously had no row cap at all. Seed more than one page's worth
        // of diff rows for a single stable key (a unique (snapshot_id, entity_type, entity_stable_key)
        // index means each row needs its own snapshot) and prove both the cap and full-walk pagination.
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        const string stableKey = "dev-1|SER-1";
        var sharedInstant = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        const int rowCount = 205;
        const int pageSize = 200;

        var expected = new HashSet<Guid>();
        await using (var context = _fixture.CreateContext())
        {
            for (var i = 0; i < rowCount; i++)
            {
                var snapshotId = Guid.NewGuid();
                context.Snapshots.Add(new TopologySnapshot(
                    snapshotId, rackId, sharedInstant, "svc", "chr", Guid.NewGuid(),
                    SnapshotStatus.Completed, version: i + 1));

                var diffId = Guid.NewGuid();
                expected.Add(diffId);
                context.EntityDiffs.Add(new TopologyEntityDiff(
                    diffId, rackId, snapshotId, TopologyEntityType.Switch, stableKey, ChangeType.Modified,
                    "{\"changed\":{}}", sharedInstant, Guid.NewGuid()));
            }

            await context.SaveChangesAsync();
        }

        await using (var capped = _fixture.CreateContext())
        {
            var firstPage = await capped.EntityHistoryAsync(
                rackId, TopologyEntityType.Switch, stableKey, after: null, pageSize, default);
            firstPage.Should().HaveCount(pageSize, "the cap must bind even when every row shares a timestamp");
        }

        var seen = await PageAllEntityHistoryAsync(rackId, TopologyEntityType.Switch, stableKey, pageSize: 50);
        seen.Should().BeEquivalentTo(expected);
    }

    private async Task<HashSet<Guid>> PageAllEntityHistoryAsync(
        Guid rackId, TopologyEntityType entityType, string stableKey, int pageSize)
    {
        var seen = new HashSet<Guid>();
        KeysetPosition? after = null;
        await using var context = _fixture.CreateContext();
        while (true)
        {
            var page = await context.EntityHistoryAsync(rackId, entityType, stableKey, after, pageSize + 1);
            var hasMore = page.Count > pageSize;
            foreach (var row in page.Take(pageSize))
            {
                seen.Add(row.Id).Should().BeTrue("no diff row should be returned on two different pages");
            }

            if (!hasMore)
            {
                return seen;
            }

            var last = page[pageSize - 1];
            after = Decode(
                CursorCodec.Encode(last.CreatedAtUtc, last.Id, rackId, "topology.entity.history"),
                rackId, "topology.entity.history");
        }
    }

    // Pages the whole audit trail using the same keyset-cursor protocol the controller uses (fetch
    // limit+1, trim, encode the last row's (timestamp, id) as the next cursor).
    private async Task<HashSet<Guid>> PageAllAuditAsync(Guid rackId, int pageSize)
    {
        var seen = new HashSet<Guid>();
        KeysetPosition? after = null;
        await using var context = _fixture.CreateContext();
        while (true)
        {
            var page = await context.AuditPageAsync(
                rackId, DateTime.UnixEpoch, DateTime.UtcNow.AddYears(1), after, pageSize + 1);
            var hasMore = page.Count > pageSize;
            foreach (var row in page.Take(pageSize))
            {
                seen.Add(row.Id).Should().BeTrue("no audit row should be returned on two different pages");
            }

            if (!hasMore)
            {
                return seen;
            }

            var last = page[pageSize - 1];
            after = Decode(CursorCodec.Encode(last.OccurredAtUtc, last.Id, rackId, "audit.list"), rackId, "audit.list");
        }
    }

    private async Task<HashSet<Guid>> PageAllSnapshotHistoryAsync(Guid rackId, int pageSize)
    {
        var seen = new HashSet<Guid>();
        KeysetPosition? after = null;
        await using var context = _fixture.CreateContext();
        while (true)
        {
            var page = await context.SnapshotHistoryPageAsync(rackId, after, pageSize + 1);
            var hasMore = page.Count > pageSize;
            foreach (var row in page.Take(pageSize))
            {
                seen.Add(row.Id).Should().BeTrue("no snapshot should be returned on two different pages");
            }

            if (!hasMore)
            {
                return seen;
            }

            var last = page[pageSize - 1];
            after = Decode(CursorCodec.Encode(last.CreatedAtUtc, last.Id, rackId, "topology.snapshots.history"), rackId, "topology.snapshots.history");
        }
    }

    private static KeysetPosition Decode(string cursor, Guid rackId, string endpoint)
    {
        CursorCodec.TryDecode(cursor, rackId, endpoint, out var ts, out var id).Should().BeTrue();
        return new KeysetPosition(ts, id);
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
