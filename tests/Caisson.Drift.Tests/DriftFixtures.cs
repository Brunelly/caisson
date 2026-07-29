using Caisson.Domain.DesiredState;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;

namespace Caisson.Drift.Tests;

/// <summary>Shared test fixtures for the drift engine tests: a matched rack/switch/port pair on both sides.</summary>
internal static class DriftFixtures
{
    public const string RackSlug = "rack-1";
    public const string SwitchName = "sw1";
    public const string PortName = "ether1";

    public static DesiredStateTree Desired(
        int accessVlan = 10,
        string? neighborSystemName = null,
        string? neighborPortId = null,
        string switchName = SwitchName,
        string portName = PortName,
        bool includePort = true)
    {
        var version = new DesiredStateVersion(
            Guid.NewGuid(), RackSlug, "a".PadLeft(40, '0'), Guid.NewGuid(), DateTime.UtcNow, "hash-1",
            "{}", 1, "desired-state-ingestion");
        var rackIntent = new DesiredRackIntent(Guid.NewGuid(), version.Id, RackSlug, "rack-stable-key");
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rackIntent.Id, switchName, "switch-stable-key");

        var ports = includePort
            ? new[]
            {
                new DesiredPortIntent(
                    Guid.NewGuid(), switchIntent.Id, portName, "port-stable-key", accessVlan,
                    description: null, neighborSystemName, neighborPortId),
            }
            : Array.Empty<DesiredPortIntent>();

        return new DesiredStateTree(version, rackIntent, new[] { switchIntent }, ports);
    }

    public static TopologySnapshot Observed(
        Guid rackId,
        int? pvid = 10,
        int[]? taggedVlans = null,
        string? neighborSystemName = null,
        string? neighborChassisId = null,
        string? neighborPortId = null,
        string switchName = SwitchName,
        string portName = PortName,
        bool includePort = true)
    {
        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);
        var sw = new Switch(Guid.NewGuid(), rackId, snapshot.Id, DateTime.UtcNow, switchName);

        if (includePort)
        {
            var port = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshot.Id, portName, isUp: true, pvid: pvid, taggedVlans: taggedVlans);

            if (neighborSystemName is not null || neighborChassisId is not null || neighborPortId is not null)
            {
                port.AddLldpNeighbour(new LldpNeighbour(
                    Guid.NewGuid(), port.Id, rackId, snapshot.Id,
                    neighborChassisId ?? "chassis-1", neighborPortId ?? "remote-port-1", neighborSystemName));
            }

            sw.AddPort(port);
        }

        snapshot.AddSwitch(sw);
        return snapshot;
    }

    /// <summary>A snapshot with one NIC ambiguously correlated to two distinct candidate switch ports.</summary>
    public static TopologySnapshot ObservedWithAmbiguousNic(Guid rackId, string mac = "aa:aa:aa:aa:aa:01")
    {
        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var server = new Server(Guid.NewGuid(), rackId, snapshot.Id, BmcType.Redfish, "10.0.1.1", "srv1");
        var nic = new Nic(Guid.NewGuid(), server.Id, rackId, snapshot.Id, "eth0", MacAddressValue.Parse(mac));
        server.AddNic(nic);
        snapshot.AddServer(server);

        var sw = new Switch(Guid.NewGuid(), rackId, snapshot.Id, DateTime.UtcNow, SwitchName);
        var port1 = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshot.Id, "ether1");
        var port2 = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshot.Id, "ether2");
        sw.AddPort(port1);
        sw.AddPort(port2);
        snapshot.AddSwitch(sw);

        snapshot.AddCandidateMapping(new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshot.Id, nic.Id, ConfidenceScore.From(0.5), ReasonCode.MultipleMacPorts, port1.Id));
        snapshot.AddCandidateMapping(new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshot.Id, nic.Id, ConfidenceScore.From(0.5), ReasonCode.MultipleMacPorts, port2.Id));

        return snapshot;
    }

    /// <summary>A snapshot with one NIC that has no candidate switch port at all (unmapped).</summary>
    public static TopologySnapshot ObservedWithUnmappedNic(Guid rackId, string mac = "aa:aa:aa:aa:aa:02")
    {
        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var server = new Server(Guid.NewGuid(), rackId, snapshot.Id, BmcType.Redfish, "10.0.1.2", "srv2");
        var nic = new Nic(Guid.NewGuid(), server.Id, rackId, snapshot.Id, "eth0", MacAddressValue.Parse(mac));
        server.AddNic(nic);
        snapshot.AddServer(server);

        snapshot.AddCandidateMapping(new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshot.Id, nic.Id, ConfidenceScore.From(0.0), ReasonCode.NotSeenInSwitch, switchPortId: null));

        return snapshot;
    }

    /// <summary>A snapshot with a single, uncontested NIC-to-port candidate (no drift expected).</summary>
    public static TopologySnapshot ObservedWithCleanNic(Guid rackId, string mac = "aa:aa:aa:aa:aa:03")
    {
        var snapshot = new TopologySnapshot(
            Guid.NewGuid(), rackId, DateTime.UtcNow, "tester", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var server = new Server(Guid.NewGuid(), rackId, snapshot.Id, BmcType.Redfish, "10.0.1.3", "srv3");
        var nic = new Nic(Guid.NewGuid(), server.Id, rackId, snapshot.Id, "eth0", MacAddressValue.Parse(mac));
        server.AddNic(nic);
        snapshot.AddServer(server);

        var sw = new Switch(Guid.NewGuid(), rackId, snapshot.Id, DateTime.UtcNow, SwitchName);
        var port = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshot.Id, "ether3");
        sw.AddPort(port);
        snapshot.AddSwitch(sw);

        snapshot.AddCandidateMapping(new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshot.Id, nic.Id, ConfidenceScore.From(0.95), ReasonCode.MacLearnUnique, port.Id));

        return snapshot;
    }
}
