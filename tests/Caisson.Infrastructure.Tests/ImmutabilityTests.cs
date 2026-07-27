using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Proves the runtime append-only guard: a persisted snapshot or snapshot-scoped entity cannot be
/// modified in place — <see cref="CaissonDbContext.SaveChanges(bool)"/> throws (AC3).
/// </summary>
public sealed class ImmutabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public ImmutabilityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Mutating_a_persisted_snapshot_is_rejected()
    {
        await _fixture.MigrateAsync();
        var (rackId, snapshotId) = await SeedAsync();

        await using var context = _fixture.CreateContext();
        var snapshot = await context.Snapshots.SingleAsync(s => s.Id == snapshotId);

        // Force a modification via the change tracker (audit fields have no public setter).
        context.Entry(snapshot).Property(s => s.CreatedBy).CurrentValue = "tampered";

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        _ = rackId;
    }

    [Fact]
    public async Task Mutating_a_persisted_snapshot_scoped_entity_is_rejected()
    {
        await _fixture.MigrateAsync();
        var (_, snapshotId) = await SeedAsync();

        await using var context = _fixture.CreateContext();
        var server = await context.Servers.FirstAsync(s => s.SnapshotId == snapshotId);

        context.Entry(server).Property(s => s.Hostname).CurrentValue = "renamed";

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private async Task<(Guid RackId, Guid SnapshotId)> SeedAsync()
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));

        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);
        var server = new Server(serverId, rackId, snapshotId, BmcType.Redfish, "10.0.1.1", hostname: "node-0");
        server.AddNic(new Nic(
            Guid.NewGuid(), serverId, rackId, snapshotId, "eth0", MacAddressValue.Parse("001122334455")));
        snapshot.AddServer(server);
        context.Snapshots.Add(snapshot);

        await context.SaveChangesAsync();
        return (rackId, snapshotId);
    }
}
