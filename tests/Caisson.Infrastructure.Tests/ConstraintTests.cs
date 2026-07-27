using System.Reflection;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Verifies the database-level guarantees (AC2, AC4): the confidence CHECK constraint, per-snapshot
/// unique natural keys, and that duplicate MACs within a snapshot are intentionally permitted.
/// </summary>
public sealed class ConstraintTests : IClassFixture<PostgresFixture>
{
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";

    private readonly PostgresFixture _fixture;

    public ConstraintTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Confidence_outside_the_bound_is_rejected_by_the_check_constraint()
    {
        await _fixture.MigrateAsync();
        var (rackId, snapshotId, nicId) = await SeedRackSnapshotAndNicAsync();

        // Bypass the value object's own guard to prove the DB is the last line of defence (ADR 0004).
        var outOfRange = ForceConfidence(1.5);
        var mapping = new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshotId, nicId, outOfRange, ReasonCode.Unknown);

        await using var context = _fixture.CreateContext();
        context.CandidateMappings.Add(mapping);

        var act = async () => await context.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        assertion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task Duplicate_serial_within_a_snapshot_violates_the_scoped_unique_index()
    {
        await _fixture.MigrateAsync();
        var (rackId, snapshotId, _) = await SeedRackSnapshotAndNicAsync();

        await using var context = _fixture.CreateContext();
        context.Switches.Add(new Switch(Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, serial: "DUP-1"));
        context.Switches.Add(new Switch(Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, serial: "DUP-1"));

        var act = async () => await context.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        assertion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(UniqueViolation);
    }

    [Fact]
    public async Task Duplicate_mac_within_a_snapshot_is_allowed()
    {
        await _fixture.MigrateAsync();
        var (rackId, snapshotId, _) = await SeedRackSnapshotAndNicAsync();

        var mac = MacAddressValue.Parse("aa:bb:cc:dd:ee:ff");
        await using var context = _fixture.CreateContext();
        context.MacAddresses.Add(new MacAddress(
            Guid.NewGuid(), rackId, snapshotId, mac, MacSource.Bmc, DateTime.UtcNow));
        context.MacAddresses.Add(new MacAddress(
            Guid.NewGuid(), rackId, snapshotId, mac, MacSource.Switch, DateTime.UtcNow));

        var act = async () => await context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
        (await context.MacAddresses.CountAsync(m => m.SnapshotId == snapshotId && m.Mac == mac))
            .Should().Be(2);
    }

    private async Task<(Guid RackId, Guid SnapshotId, Guid NicId)> SeedRackSnapshotAndNicAsync()
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var nicId = Guid.NewGuid();

        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));

        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);
        var server = new Server(serverId, rackId, snapshotId, BmcType.Redfish, "10.0.1.1");
        server.AddNic(new Nic(
            nicId, serverId, rackId, snapshotId, "eth0", MacAddressValue.Parse("001122334455")));
        snapshot.AddServer(server);
        context.Snapshots.Add(snapshot);

        await context.SaveChangesAsync();
        return (rackId, snapshotId, nicId);
    }

    private static ConfidenceScore ForceConfidence(double value)
    {
        var ctor = typeof(ConfidenceScore).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null, new[] { typeof(double) }, modifiers: null);
        return (ConfidenceScore)ctor!.Invoke(new object[] { value });
    }
}
