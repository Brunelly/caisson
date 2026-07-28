using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.Persistence.Ingestion;

namespace Caisson.Api.IntegrationTests;

/// <summary>The identifiers of the topology seeded for the API integration tests.</summary>
public sealed record SeededTopology(
    Guid RackId,
    Guid FirstSnapshotId,
    int FirstVersion,
    Guid SecondSnapshotId,
    int SecondVersion)
{
    /// <summary>An entity type present in the seed with change history.</summary>
    public string ServerEntityType => "Server";

    /// <summary>The stable key of a seeded server that was modified between v1 and v2.</summary>
    public string ServerStableKey => "uuid-1";
}

/// <summary>Seeds a rack with two snapshots (the second modifies a server) via the real ingestion service.</summary>
internal static class SeedData
{
    private static readonly DateTime V1At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime V2At = new(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);

    public static async Task<SeededTopology> SeedAsync(PostgresHarness harness)
    {
        var rackId = Guid.NewGuid();
        await using (var context = harness.CreateContext())
        {
            context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        SnapshotIngestionOutcome first, second;
        await using (var context = harness.CreateContext())
        {
            var service = new TopologySnapshotIngestionService(context, new GuidTopologyIdGenerator());
            first = await service.IngestAsync(Request(rackId, Observed("node-1"), Correlation(), V1At));
        }

        await using (var context = harness.CreateContext())
        {
            var service = new TopologySnapshotIngestionService(context, new GuidTopologyIdGenerator());
            second = await service.IngestAsync(Request(rackId, Observed("node-1-renamed"), Correlation(), V2At));
        }

        return new SeededTopology(rackId, first.SnapshotId, first.Version, second.SnapshotId, second.Version);
    }

    private static TopologyIngestionRequest Request(
        Guid rackId, TopologyCorrelationInput observed, TopologyCorrelationResult correlation, DateTime at)
        => new(
            rackId, observed, correlation, TriggerType.Scheduled, "svc-discovery", ActorType.ServiceAccount,
            "chr", "7.15", Guid.NewGuid(), SnapshotStatus.Completed, at, at);

    private static TopologyCorrelationInput Observed(string server1Hostname)
    {
        var sw = new SwitchTopologySnapshot(
            "sw1",
            new SwitchDeviceInfo("10.0.0.1", "SW-1", "CRS354", "7.15"),
            new List<SwitchPortInfo>
            {
                new("ether1", true, 10, new[] { 10 }),
                new("ether2", true, 20, new[] { 20 }),
                new("ether3", false, 1, Array.Empty<int>()),
                // A port name containing '/' (e.g. a stacked-switch port id) — its stable key carries a
                // slash so it exercises the catch-all entity route (that a single-segment route would 404).
                new("1/1/1", true, 30, Array.Empty<int>()),
            },
            new List<LldpNeighbourInfo>(),
            new List<BridgeHostEntry>
            {
                new("ether1", MacAddressValue.Parse("aa:aa:aa:aa:aa:a1")),
                new("ether2", MacAddressValue.Parse("aa:aa:aa:aa:aa:a2")),
            },
            new List<VlanInfo> { new(10, "data"), new(20, "storage") });

        var server1 = new ServerNicSnapshot(
            "srv1",
            new BmcSystemInventory(BmcType.Redfish, "10.0.1.1", "uuid-1", server1Hostname),
            new List<BmcNetworkInterfaceInfo> { new("eth0", MacAddressValue.Parse("aa:aa:aa:aa:aa:a1"), LinkState.Up) });

        var server2 = new ServerNicSnapshot(
            "srv2",
            new BmcSystemInventory(BmcType.Redfish, "10.0.1.2", "uuid-2", "node-2"),
            new List<BmcNetworkInterfaceInfo> { new("eth0", MacAddressValue.Parse("aa:aa:aa:aa:aa:a2"), LinkState.Up) });

        return new TopologyCorrelationInput(new[] { sw }, new[] { server1, server2 });
    }

    private static TopologyCorrelationResult Correlation()
    {
        var m1 = new NicPortMapping(
            "srv1", "eth0", MacAddressValue.Parse("aa:aa:aa:aa:aa:a1"),
            new PortCandidate("sw1", "ether1", ConfidenceScore.From(0.9), new[] { 10 }, new[] { ReasonCode.MacLearnUnique }));
        var m2 = new NicPortMapping(
            "srv2", "eth0", MacAddressValue.Parse("aa:aa:aa:aa:aa:a2"),
            new PortCandidate("sw1", "ether2", ConfidenceScore.From(0.85), new[] { 20 }, new[] { ReasonCode.MacLearnUnique }));

        return new TopologyCorrelationResult(
            new[] { m1, m2 },
            Array.Empty<AmbiguousNicMapping>(),
            Array.Empty<UnmappedNic>(),
            new[] { new UnmappedPort("sw1", "ether3", new[] { ReasonCode.NotSeenInBmc }) });
    }
}
