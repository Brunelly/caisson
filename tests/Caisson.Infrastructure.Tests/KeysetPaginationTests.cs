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
            after = Decode(CursorCodec.Encode(last.OccurredAtUtc, last.Id));
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
            after = Decode(CursorCodec.Encode(last.CreatedAtUtc, last.Id));
        }
    }

    private static KeysetPosition Decode(string cursor)
    {
        CursorCodec.TryDecode(cursor, out var ts, out var id).Should().BeTrue();
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
