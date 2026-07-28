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

        var sw = new Switch(Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, serial: "SW-1");
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
}
