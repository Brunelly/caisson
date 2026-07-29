using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Persists a realistic-sized topology snapshot and proves the graph traversals the schema exists to
/// serve (server->nic->mac and switch->port->lldp), plus deterministic latest-per-rack selection with
/// older snapshots still queryable (AC1, AC3, NFR1).
/// </summary>
public sealed class PersistenceAndTraversalTests : IClassFixture<PostgresFixture>
{
    private const int SwitchCount = 2;
    private const int PortsPerSwitch = 48;
    private const int ServerCount = 20;
    private const int NicsPerServer = 4;

    private readonly PostgresFixture _fixture;

    public PersistenceAndTraversalTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Realistic_snapshot_persists_and_supports_both_graph_traversals()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        var snapshot = BuildSnapshot(rackId, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), version: 1);
        await using (var context = _fixture.CreateContext())
        {
            context.Snapshots.Add(snapshot);
            await context.SaveChangesAsync();
        }

        // server -> nic -> mac
        await using (var context = _fixture.CreateContext())
        {
            var servers = await context.Servers
                .Where(s => s.SnapshotId == snapshot.Id)
                .Include(s => s.Nics)
                .ThenInclude(n => n.MacAddresses)
                .ToListAsync();

            servers.Should().HaveCount(ServerCount);
            servers.Should().OnlyContain(s => s.Nics.Count == NicsPerServer);
            servers.SelectMany(s => s.Nics).Should()
                .OnlyContain(n => n.MacAddresses.Count == 1);
        }

        // switch -> port -> lldp neighbour
        await using (var context = _fixture.CreateContext())
        {
            var switches = await context.Switches
                .Where(s => s.SnapshotId == snapshot.Id)
                .Include(s => s.Ports)
                .ThenInclude(p => p.LldpNeighbours)
                .ToListAsync();

            switches.Should().HaveCount(SwitchCount);
            switches.Should().OnlyContain(s => s.Ports.Count == PortsPerSwitch);
            switches.SelectMany(s => s.Ports).SelectMany(p => p.LldpNeighbours)
                .Should().HaveCount(SwitchCount); // one uplink neighbour per switch
        }
    }

    [Fact]
    public async Task Latest_snapshot_per_rack_is_deterministic_and_older_remains_queryable()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        var older = BuildSnapshot(rackId, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), version: 1);
        var newer = BuildSnapshot(rackId, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), version: 2);

        await using (var context = _fixture.CreateContext())
        {
            context.Snapshots.Add(older);
            context.Snapshots.Add(newer);
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var latest = await context.LatestSnapshotForRackAsync(rackId);
            stopwatch.Stop();

            latest.Should().NotBeNull();
            latest!.Id.Should().Be(newer.Id);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1)); // NFR1 (dev/CI-sized)

            // Older snapshot remains fully queryable for audit/history.
            (await context.Snapshots.FindAsync(older.Id)).Should().NotBeNull();
        }
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private static TopologySnapshot BuildSnapshot(Guid rackId, DateTime createdAtUtc, int version = 1)
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, createdAtUtc, "svc-discovery", "chr",
            Guid.NewGuid(), SnapshotStatus.Completed, sourceVersion: "7.15",
            version: version, triggerType: TriggerType.Scheduled,
            startedAtUtc: createdAtUtc, completedAtUtc: createdAtUtc);

        for (var s = 0; s < SwitchCount; s++)
        {
            var sw = new Switch(
                Guid.NewGuid(), rackId, snapshotId, createdAtUtc, externalDeviceKey: $"sw-{s}",
                managementIp: $"10.0.0.{s + 1}", serial: $"SW-SERIAL-{s}", model: "CRS354", osVersion: "7.15");

            for (var p = 0; p < PortsPerSwitch; p++)
            {
                var port = new SwitchPort(
                    Guid.NewGuid(), sw.Id, rackId, snapshotId, $"ether{p + 1}",
                    isUp: true, pvid: 1, taggedVlans: new[] { 10, 20 });

                if (p == 0)
                {
                    port.AddLldpNeighbour(new LldpNeighbour(
                        Guid.NewGuid(), port.Id, rackId, snapshotId,
                        chassisId: $"chassis-{s}", portId: "uplink", systemName: $"spine-{s}"));
                }

                sw.AddPort(port);
            }

            snapshot.AddSwitch(sw);
        }

        for (var s = 0; s < ServerCount; s++)
        {
            var server = new Server(
                Guid.NewGuid(), rackId, snapshotId, BmcType.Redfish,
                bmcAddress: $"10.0.1.{s + 1}", externalDeviceKey: $"srv-{s}",
                bmcUuid: Guid.NewGuid().ToString(), hostname: $"node-{s}");

            for (var n = 0; n < NicsPerServer; n++)
            {
                var mac = MacAddressValue.Parse($"0000{s:x4}{n:x4}");
                var nic = new Nic(
                    Guid.NewGuid(), server.Id, rackId, snapshotId, $"eth{n}", mac, LinkState.Up);
                nic.AddMacAddress(new MacAddress(
                    Guid.NewGuid(), rackId, snapshotId, mac, MacSource.Bmc, createdAtUtc, nic.Id));
                server.AddNic(nic);
            }

            snapshot.AddServer(server);
        }

        snapshot.AddVlan(new Vlan(Guid.NewGuid(), rackId, snapshotId, 10, "data"));
        snapshot.AddVlan(new Vlan(Guid.NewGuid(), rackId, snapshotId, 20, "storage"));

        return snapshot;
    }
}
