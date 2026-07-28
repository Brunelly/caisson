using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.Persistence.Ingestion;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// A small but representative hand-built correlation input + result, and a deterministic id generator,
/// shared by the DB-free bridge/diff/shaping unit tests. It exercises every mapper branch: a confident
/// mapping, an ambiguous mapping, an unmapped NIC (both MAC-bearing and MAC-less), and an unmapped port.
/// </summary>
internal static class IngestionTestData
{
    public const string MacA = "aa:aa:aa:aa:aa:a1";
    public const string MacB = "aa:aa:aa:aa:aa:a2";
    public const string MacC = "aa:aa:aa:aa:aa:a3";

    /// <summary>Observed input: one switch with four ports and two servers (one NIC MAC-less).</summary>
    public static TopologyCorrelationInput Observed()
    {
        var switchSnapshot = new SwitchTopologySnapshot(
            SwitchId: "sw1",
            Device: new SwitchDeviceInfo("10.0.0.1", "SW-1", "CRS354", "7.15"),
            Ports: new List<SwitchPortInfo>
            {
                new("ether1", true, 10, new[] { 10 }),
                new("ether2", true, 20, new[] { 20 }),
                new("ether3", true, 20, new[] { 20 }),
                new("ether4", false, 1, Array.Empty<int>()),
            },
            LldpNeighbours: new List<LldpNeighbourInfo>
            {
                new("ether3", "chassis-spine", "eth9", "spine-0"),
            },
            BridgeHosts: new List<BridgeHostEntry>
            {
                new("ether1", MacAddressValue.Parse(MacA)),
                new("ether2", MacAddressValue.Parse(MacB)),
            },
            Vlans: new List<VlanInfo> { new(10, "data"), new(20, "storage") });

        var server1 = new ServerNicSnapshot(
            "srv1",
            new BmcSystemInventory(BmcType.Redfish, "10.0.1.1", "uuid-1", "node-1"),
            new List<BmcNetworkInterfaceInfo> { new("eth0", MacAddressValue.Parse(MacA), LinkState.Up) });

        var server2 = new ServerNicSnapshot(
            "srv2",
            new BmcSystemInventory(BmcType.Redfish, "10.0.1.2", "uuid-2", "node-2"),
            new List<BmcNetworkInterfaceInfo>
            {
                new("eth0", MacAddressValue.Parse(MacB), LinkState.Up),
                new("eth1", MacAddressValue.Parse(MacC), LinkState.Down),
                new("eth2", null, LinkState.Unknown), // MAC-less NIC — engine reports it unmapped.
            });

        return new TopologyCorrelationInput(
            new[] { switchSnapshot }, new[] { server1, server2 });
    }

    /// <summary>Correlation result over <see cref="Observed"/>.</summary>
    public static TopologyCorrelationResult Correlation()
    {
        var confident = new NicPortMapping(
            "srv1", "eth0", MacAddressValue.Parse(MacA),
            new PortCandidate("sw1", "ether1", ConfidenceScore.From(0.92), new[] { 10 },
                new[] { ReasonCode.MacLearnUnique, ReasonCode.LldpConsistent }));

        var ambiguous = new AmbiguousNicMapping(
            "srv2", "eth0", MacAddressValue.Parse(MacB),
            new List<PortCandidate>
            {
                new("sw1", "ether2", ConfidenceScore.From(0.60), new[] { 20 }, new[] { ReasonCode.MultipleMacPorts }),
                new("sw1", "ether3", ConfidenceScore.From(0.55), new[] { 20 }, new[] { ReasonCode.MultipleMacPorts }),
            });

        var unmappedNic = new UnmappedNic("srv2", "eth1", new[] { ReasonCode.NotSeenInSwitch });
        var unmappedNicNoMac = new UnmappedNic("srv2", "eth2", new[] { ReasonCode.ParseError });
        var unmappedPort = new UnmappedPort("sw1", "ether4", new[] { ReasonCode.NotSeenInBmc });

        return new TopologyCorrelationResult(
            new[] { confident },
            new[] { ambiguous },
            new[] { unmappedNic, unmappedNicNoMac },
            new[] { unmappedPort });
    }

    /// <summary>A deterministic run context (fixed timestamps, version 1).</summary>
    public static SnapshotRunContext RunContext(int version = 1)
    {
        var at = new DateTime(2026, 7, 28, 4, 0, 0, DateTimeKind.Utc);
        return new SnapshotRunContext(
            version, TriggerType.OnDemand, "svc-discovery", "chr", "7.15",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), SnapshotStatus.Completed, at, at, at);
    }

    /// <summary>A deterministic, monotonic id generator so mapper/diff output is reproducible in tests.</summary>
    public sealed class SequentialIds : ITopologyIdGenerator
    {
        private int _counter;

        public Guid NewId()
        {
            _counter++;
            var bytes = new byte[16];
            BitConverter.GetBytes(_counter).CopyTo(bytes, 0);
            return new Guid(bytes);
        }
    }
}
