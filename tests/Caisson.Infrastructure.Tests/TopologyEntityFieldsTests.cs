using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of <see cref="TopologyEntityFields.Extract"/> — in particular that it tolerates an LLDP
/// neighbour with no stable identity rather than throwing (which, running inside the diff during an
/// all-or-nothing ingestion, would lose the whole snapshot). No database required.
/// </summary>
public sealed class TopologyEntityFieldsTests
{
    [Fact]
    public void Extract_skips_lldp_neighbours_with_an_empty_stable_key_without_throwing()
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var sw = new Switch(Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, externalDeviceKey: "sw-1", serial: "SW-1");
        var port = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshotId, "ether1");
        // One well-formed neighbour and one that omitted its port id (empty) — the latter has no stable key.
        port.AddLldpNeighbour(new LldpNeighbour(
            Guid.NewGuid(), port.Id, rackId, snapshotId, "chassis-good", "port-good"));
        port.AddLldpNeighbour(new LldpNeighbour(
            Guid.NewGuid(), port.Id, rackId, snapshotId, "chassis-bad", string.Empty));
        sw.AddPort(port);
        snapshot.AddSwitch(sw);

        var extract = TopologyEntityFields.Extract(snapshot);

        var lldp = extract[TopologyEntityType.Lldp];
        lldp.Should().ContainKey("chassis-good|port-good");
        lldp.Should().HaveCount(1); // the malformed neighbour is skipped, not persisted or thrown on
    }

    [Fact]
    public void Two_switches_reporting_an_identical_serial_but_distinct_device_keys_survive_as_distinct_entities()
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        snapshot.AddSwitch(new Switch(
            Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, externalDeviceKey: "sw-a", serial: "SAME-SERIAL"));
        snapshot.AddSwitch(new Switch(
            Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, externalDeviceKey: "sw-b", serial: "SAME-SERIAL"));

        var extract = TopologyEntityFields.Extract(snapshot, out var collisions);

        extract[TopologyEntityType.Switch].Should().HaveCount(2, "distinct configured devices never collide onto one stable key");
        collisions.Should().BeEmpty();
    }

    [Fact]
    public void Two_servers_reporting_an_identical_uuid_but_distinct_device_keys_survive_as_distinct_entities()
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        snapshot.AddServer(new Server(
            Guid.NewGuid(), rackId, snapshotId, BmcType.Redfish, "10.0.1.1", externalDeviceKey: "srv-a", bmcUuid: "SAME-UUID"));
        snapshot.AddServer(new Server(
            Guid.NewGuid(), rackId, snapshotId, BmcType.Redfish, "10.0.1.2", externalDeviceKey: "srv-b", bmcUuid: "SAME-UUID"));

        var extract = TopologyEntityFields.Extract(snapshot, out var collisions);

        extract[TopologyEntityType.Server].Should().HaveCount(2, "distinct configured devices never collide onto one stable key");
        collisions.Should().BeEmpty();
    }

    [Fact]
    public void A_genuine_stable_key_collision_is_skipped_and_reported_rather_than_silently_overwriting()
    {
        // Two VLANs sharing the same rack-scoped id (a real, documented collapse case per StableKeys'
        // remarks) exercises the TryAdd skip-and-report path directly, without needing two devices to
        // agree on every field of a composite key.
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        snapshot.AddVlan(new Vlan(Guid.NewGuid(), rackId, snapshotId, 10, "vlan-a"));
        snapshot.AddVlan(new Vlan(Guid.NewGuid(), rackId, snapshotId, 10, "vlan-b"));

        var extract = TopologyEntityFields.Extract(snapshot, out var collisions);

        extract[TopologyEntityType.Vlan].Should().HaveCount(1, "the second write is skipped, not merged");
        collisions.Should().ContainSingle(c => c.EntityType == TopologyEntityType.Vlan && c.StableKey == "10");
    }
}
