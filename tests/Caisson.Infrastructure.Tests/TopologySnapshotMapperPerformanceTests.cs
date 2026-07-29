using System.Diagnostics;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Infrastructure.Persistence.Ingestion;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Finding #13: the LLDP-to-port join in <c>TopologySnapshotMapper.MapSwitches</c> used to scan
/// <c>sw.Ports</c> linearly for every LLDP neighbour — quadratic in device-controlled counts, and the one
/// true quadratic in an otherwise-linear ingestion pipeline. This asserts the mapper stays inside a
/// generous time budget for a 20k-port / 20k-neighbour single switch, which a quadratic implementation
/// would blow through by orders of magnitude.
/// </summary>
public sealed class TopologySnapshotMapperPerformanceTests
{
    private static readonly Guid RackId = Guid.NewGuid();

    [Fact]
    public void Mapping_a_20k_port_20k_neighbour_switch_stays_within_a_linear_time_budget()
    {
        const int count = 20_000;
        var ports = new List<SwitchPortInfo>(count);
        var neighbours = new List<LldpNeighbourInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var portName = $"ether{i}";
            ports.Add(new SwitchPortInfo(portName, IsUp: true, Pvid: 10, TaggedVlans: Array.Empty<int>()));
            neighbours.Add(new LldpNeighbourInfo(portName, $"chassis-{i}", $"port-{i}"));
        }

        var input = new TopologyCorrelationInput(
            Switches: new[]
            {
                new SwitchTopologySnapshot(
                    "sw-1", Device: null, Ports: ports, LldpNeighbours: neighbours,
                    BridgeHosts: Array.Empty<BridgeHostEntry>(), Vlans: Array.Empty<VlanInfo>()),
            },
            Servers: Array.Empty<ServerNicSnapshot>());

        var correlation = new TopologyCorrelationResult(
            Array.Empty<NicPortMapping>(), Array.Empty<AmbiguousNicMapping>(),
            Array.Empty<UnmappedNic>(), Array.Empty<UnmappedPort>());

        var runContext = new SnapshotRunContext(
            1, TriggerType.OnDemand, "svc", "chr", null, Guid.NewGuid(), SnapshotStatus.Completed,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

        var stopwatch = Stopwatch.StartNew();
        var mapped = TopologySnapshotMapper.Map(RackId, runContext, input, correlation, Guid.NewGuid);
        stopwatch.Stop();

        mapped.Snapshot.Switches.Single().Ports.Should().HaveCount(count);
        mapped.Snapshot.Switches.Single().Ports.Sum(p => p.LldpNeighbours.Count).Should().Be(count);
        // A quadratic 20k x 20k FirstOrDefault scan is ~400M comparisons — multiple seconds even on fast
        // hardware. A generous linear budget catches a regression without being flaky on a loaded CI box.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
