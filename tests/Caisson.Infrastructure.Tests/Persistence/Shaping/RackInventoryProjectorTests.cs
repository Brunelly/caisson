using Caisson.Domain.Enums;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;
using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests.Persistence.Shaping;

/// <summary>
/// Tests for the pure rack-inventory projector (story #170, Step 2): stable-key mapping, LLDP/tagged-VLAN/
/// Pvid/IsUp carry-through, port-role classification reusing <c>Caisson.Correlation.PortRoleClassifier</c>,
/// management composed on top, and empty-when-no-usable-snapshot.
/// </summary>
public sealed class RackInventoryProjectorTests
{
    private static readonly Guid RackId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SnapshotId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void No_snapshot_yields_an_empty_inventory()
    {
        var inventory = RackInventoryProjector.Project(RackId, snapshot: null);

        inventory.HasSnapshot.Should().BeFalse();
        inventory.Switches.Should().BeEmpty();
        inventory.RackId.Should().Be(RackId);
    }

    [Fact]
    public void A_failed_snapshot_yields_an_empty_inventory()
    {
        var snapshot = new TopologySnapshot(
            SnapshotId, RackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Failed);

        RackInventoryProjector.Project(RackId, snapshot).HasSnapshot.Should().BeFalse();
    }

    [Fact]
    public void It_maps_switches_and_ports_by_stable_key_carrying_lldp_tagged_pvid_and_isup()
    {
        var snapshot = BuildSnapshot();
        var sw1 = snapshot.Switches.Single(s => s.ExternalDeviceKey == "sw1");
        var expectedSwitchKey = StableKeys.ForSwitch(sw1);

        var inventory = RackInventoryProjector.Project(RackId, snapshot);

        inventory.SnapshotId.Should().Be(SnapshotId);
        var invSwitch = inventory.FindSwitch(expectedSwitchKey);
        invSwitch.Should().NotBeNull();

        var trunk = invSwitch!.FindPort("ether2")!;
        trunk.StableKey.Should().Be(StableKeys.ForSwitchPort(expectedSwitchKey, "ether2"));
        trunk.TaggedVlans.Should().Equal(10, 20);
        trunk.Pvid.Should().Be(1);
        trunk.IsUp.Should().BeTrue();

        var uplink = invSwitch.FindPort("uplink1")!;
        uplink.Lldp.Should().ContainSingle(n => n.ChassisId == "sw2");
    }

    [Fact]
    public void Trunk_and_uplink_classification_reuses_the_shared_port_role_rule()
    {
        var snapshot = BuildSnapshot();
        var sw1Key = StableKeys.ForSwitch(snapshot.Switches.Single(s => s.ExternalDeviceKey == "sw1"));
        var inventory = RackInventoryProjector.Project(RackId, snapshot);
        var sw1 = inventory.FindSwitch(sw1Key)!;

        sw1.FindPort("ether1")!.Role.Should().Be(PortRole.Access);
        sw1.FindPort("ether2")!.Role.Should().Be(PortRole.Uplink); // multiple tagged VLANs
        sw1.FindPort("uplink1")!.Role.Should().Be(PortRole.Uplink); // LLDP peer switch
        sw1.FindPort("uplink1")!.RoleReason.Should().Contain("another switch");
    }

    [Fact]
    public void Management_is_composed_on_top_of_the_shared_rule()
    {
        var snapshot = BuildSnapshot();
        var sw1Key = StableKeys.ForSwitch(snapshot.Switches.Single(s => s.ExternalDeviceKey == "sw1"));
        var inventory = RackInventoryProjector.Project(RackId, snapshot);
        var sw1 = inventory.FindSwitch(sw1Key)!;

        sw1.FindPort("mgmt")!.Role.Should().Be(PortRole.Management);
        sw1.FindPort("mgmt")!.RoleReason.Should().Contain("reserved management port name");
        sw1.FindPort("ether-m2")!.Role.Should().Be(PortRole.Management); // LLDP mgmt-addr == switch mgmt IP
    }

    /// <summary>A two-switch snapshot exercising access, multi-tag trunk, LLDP-uplink, and management ports.</summary>
    private static TopologySnapshot BuildSnapshot()
    {
        var snapshot = new TopologySnapshot(
            SnapshotId, RackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var sw1 = new Switch(Guid.NewGuid(), RackId, SnapshotId, DateTime.UtcNow, "sw1", "10.0.0.1", "SER1");
        sw1.AddPort(new SwitchPort(Guid.NewGuid(), sw1.Id, RackId, SnapshotId, "ether1", true, 10));
        sw1.AddPort(new SwitchPort(Guid.NewGuid(), sw1.Id, RackId, SnapshotId, "ether2", true, 1, new[] { 10, 20 }));

        var uplink = new SwitchPort(Guid.NewGuid(), sw1.Id, RackId, SnapshotId, "uplink1", true, 1);
        uplink.AddLldpNeighbour(new LldpNeighbour(
            Guid.NewGuid(), uplink.Id, RackId, SnapshotId, "sw2", "ether10", "switch-two"));
        sw1.AddPort(uplink);

        sw1.AddPort(new SwitchPort(Guid.NewGuid(), sw1.Id, RackId, SnapshotId, "mgmt", true, 99));

        var mgmtByLldp = new SwitchPort(Guid.NewGuid(), sw1.Id, RackId, SnapshotId, "ether-m2", true, 5);
        mgmtByLldp.AddLldpNeighbour(new LldpNeighbour(
            Guid.NewGuid(), mgmtByLldp.Id, RackId, SnapshotId, "host-a", "eth0", mgmtAddress: "10.0.0.1"));
        sw1.AddPort(mgmtByLldp);
        snapshot.AddSwitch(sw1);

        var sw2 = new Switch(Guid.NewGuid(), RackId, SnapshotId, DateTime.UtcNow, "sw2", "10.0.0.2", "SER2");
        sw2.AddPort(new SwitchPort(Guid.NewGuid(), sw2.Id, RackId, SnapshotId, "ether1", true, 10));
        snapshot.AddSwitch(sw2);

        return snapshot;
    }
}
