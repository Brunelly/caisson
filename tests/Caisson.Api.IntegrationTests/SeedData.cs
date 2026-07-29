using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// The stable key of a seeded server that was modified between v1 and v2. StableKeys.ForServer
    /// prefixes with the server's trusted device key ("srv1" in this fixture's Observed()) — finding #3.
    /// </summary>
    public string ServerStableKey => "srv1|uuid-1";

    /// <summary>The discovery rack seeded with a schedule and a completed job (story #8).</summary>
    public SeededDiscovery Discovery { get; init; } = null!;

    /// <summary>The drift report computed for this rack (story #64).</summary>
    public SeededDrift Drift { get; init; } = null!;
}

/// <summary>The discovery orchestration fixtures seeded for the story-8 API tests.</summary>
public sealed record SeededDiscovery(Guid RackId, string ExternalKey, Guid CompletedJobId);

/// <summary>The drift report computed for the story-64 API tests, deliberately containing a real mismatch.</summary>
public sealed record SeededDrift(Guid RackId, Guid DriftReportId, int TotalItems);

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
            var service = new TopologySnapshotIngestionService(
                context, new GuidTopologyIdGenerator(), new NoOpTopologyEventPublisher(),
                new Caisson.Infrastructure.Persistence.Drift.NoOpDriftRecomputeSignal(),
                NullLogger<TopologySnapshotIngestionService>.Instance);
            first = await service.IngestAsync(Request(rackId, Observed("node-1"), Correlation(), V1At));
        }

        await using (var context = harness.CreateContext())
        {
            var service = new TopologySnapshotIngestionService(
                context, new GuidTopologyIdGenerator(), new NoOpTopologyEventPublisher(),
                new Caisson.Infrastructure.Persistence.Drift.NoOpDriftRecomputeSignal(),
                NullLogger<TopologySnapshotIngestionService>.Instance);
            second = await service.IngestAsync(Request(rackId, Observed("node-1-renamed"), Correlation(), V2At));
        }

        var discovery = await SeedDiscoveryAsync(harness);
        var drift = await SeedDriftAsync(harness, rackId, "rack-" + rackId.ToString("N"));
        return new SeededTopology(rackId, first.SnapshotId, first.Version, second.SnapshotId, second.Version)
        {
            Discovery = discovery,
            Drift = drift,
        };
    }

    /// <summary>
    /// Seeds a desired-state revision for the already-snapshotted rack — with a deliberate access-VLAN
    /// mismatch on "ether1" (desired 99 vs. observed Pvid 10) — and computes a real drift report via the
    /// production <see cref="DriftComputationService"/>, so the API tests exercise real persisted data.
    /// </summary>
    private static async Task<SeededDrift> SeedDriftAsync(PostgresHarness harness, Guid rackId, string rackSlug)
    {
        await using (var context = harness.CreateContext())
        {
            var run = new Caisson.Domain.DesiredState.DesiredStateIngestionRun(
                Guid.NewGuid(), Caisson.Domain.DesiredState.IngestionTriggerType.Poll, DateTime.UtcNow,
                "https://example.com/repo.git", "main", Guid.NewGuid());
            run.RecordCommit("a".PadLeft(40, '0'), "author", DateTime.UtcNow, "message");
            run.Succeed(DateTime.UtcNow);
            context.DesiredStateIngestionRuns.Add(run);

            var version = new Caisson.Domain.DesiredState.DesiredStateVersion(
                Guid.NewGuid(), rackSlug, "a".PadLeft(40, '0'), run.Id, DateTime.UtcNow, "hash-" + Guid.NewGuid().ToString("N"),
                "{}", 1, "desired-state-ingestion");
            var rackIntent = new Caisson.Domain.DesiredState.DesiredRackIntent(Guid.NewGuid(), version.Id, rackSlug, "rack-key");
            var switchIntent = new Caisson.Domain.DesiredState.DesiredSwitchIntent(Guid.NewGuid(), rackIntent.Id, "sw1", "switch-key");
            var port = new Caisson.Domain.DesiredState.DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "ether1", "port-key", accessVlan: 99);

            context.DesiredStateVersions.Add(version);
            context.DesiredRackIntents.Add(rackIntent);
            context.DesiredSwitchIntents.Add(switchIntent);
            context.DesiredPortIntents.Add(port);
            await context.SaveChangesAsync();
        }

        await using var computeContext = harness.CreateContext();
        var service = new Caisson.Infrastructure.Persistence.Drift.DriftComputationService(
            computeContext, new GuidTopologyIdGenerator(), TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new Caisson.Drift.DriftComputationOptions()),
            NullLogger<Caisson.Infrastructure.Persistence.Drift.DriftComputationService>.Instance);
        await service.ComputeAndPersistAsync(rackId, Guid.NewGuid());

        await using var verify = harness.CreateContext();
        var report = await verify.DriftReports.SingleAsync(r => r.RackId == rackId);
        return new SeededDrift(rackId, report.Id, report.TotalItems);
    }

    /// <summary>Seeds a discovery rack with a disabled schedule and a completed job (story #8).</summary>
    private static async Task<SeededDiscovery> SeedDiscoveryAsync(PostgresHarness harness)
    {
        var rackId = Guid.NewGuid();
        var externalKey = "seed-discovery-rack";
        var at = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

        await using var context = harness.CreateContext();
        context.Racks.Add(new Rack(rackId, externalKey, "Discovery Seed Rack", DateTime.UtcNow));

        var job = new DiscoveryJob(
            Guid.NewGuid(), rackId, TriggerType.OnDemand, "svc-discovery", ActorType.ServiceAccount,
            Guid.NewGuid(), at);
        job.SeedSteps(Guid.NewGuid);
        job.MarkInProgress(at);
        foreach (var step in job.Steps)
        {
            step.BeginAttempt(at);
            step.Succeed(at.AddSeconds(1), "{\"discovered\":1}");
        }

        job.Succeed(at.AddSeconds(5));
        context.DiscoveryJobs.Add(job);

        context.RackDiscoverySchedules.Add(
            new RackDiscoverySchedule(rackId, enabled: false, intervalSeconds: 900, jitterSeconds: 60));

        await context.SaveChangesAsync();
        return new SeededDiscovery(rackId, externalKey, job.Id);
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
