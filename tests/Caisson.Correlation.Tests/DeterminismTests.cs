using System.Diagnostics;
using System.Text.Json;
using Caisson.Correlation.Input;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>NFR2 (determinism) and NFR3 (rack-scale performance) guards.</summary>
public sealed class DeterminismTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    private static TopologyCorrelationInput RepresentativeSnapshot() => new SnapshotBuilder()
        .Switch("sw2", s => s
            .Port("ether9", pvid: 20)
            .Lldp("ether9", systemName: "server-b")
            .Bridge("ether9", "00:11:22:33:44:aa")
            .Bridge("ether9", "00:11:22:33:44:bb"))
        .Switch("sw1", s => s
            .Port("ether1", pvid: 10)
            .Port("ether2", pvid: 10)
            .Lldp("ether1", systemName: "server-a")
            .Bridge("ether1", "00:11:22:33:44:01") // clean map for srv-a
            .Bridge("ether2", "00:11:22:33:44:aa")) // duplicate MAC across sw1/sw2 -> ambiguous
        .Server("srv-b", sv => sv.Nic("eth1", "00:11:22:33:44:aa").Nic("eth0", "de:ad:00:00:00:01"))
        .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:01"))
        .Build();

    [Fact]
    public void Twenty_runs_on_the_same_input_serialize_byte_identically()
    {
        var input = RepresentativeSnapshot();
        var baseline = JsonSerializer.Serialize(Engine.Correlate(input));

        for (var i = 0; i < 20; i++)
        {
            var run = JsonSerializer.Serialize(Engine.Correlate(input));
            run.Should().Be(baseline, "run {0} must be byte-identical to the first run", i);
        }
    }

    [Fact]
    public void Rack_scale_snapshot_correlates_well_under_the_budget()
    {
        var input = BuildRackScaleSnapshot();

        // Warm up (JIT) before measuring so the smoke test reflects steady-state cost.
        _ = Engine.Correlate(input);

        var sw = Stopwatch.StartNew();
        var result = Engine.Correlate(input);
        sw.Stop();

        // 20 servers x 4 NICs = 80 clean access mappings; the bulk of MACs live on the uplink trunks.
        result.Mappings.Should().HaveCount(80);
        // Generous CI-safe budget (the real target is <200ms); guards against accidental O(n^2).
        sw.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    private static TopologyCorrelationInput BuildRackScaleSnapshot()
    {
        var builder = new SnapshotBuilder();
        const int switchCount = 2;
        const int accessPortsPerSwitch = 40;
        const int serverCount = 20;
        const int nicsPerServer = 4;

        // Deterministic MAC from an index — no randomness (NFR2).
        static string Mac(long i) => i.ToString("x12", System.Globalization.CultureInfo.InvariantCulture);

        var nicMacs = new List<string>();
        for (var srv = 0; srv < serverCount; srv++)
        {
            for (var nic = 0; nic < nicsPerServer; nic++)
            {
                nicMacs.Add(Mac(0x0011_0000_0000L + (srv * nicsPerServer) + nic));
            }
        }

        var foreignPerUplink = 10_000 / (switchCount * 2);

        for (var sw = 0; sw < switchCount; sw++)
        {
            builder.Switch($"sw{sw}", s =>
            {
                for (var p = 0; p < accessPortsPerSwitch; p++)
                {
                    s.Port($"ether{p}", pvid: 10);
                }

                s.Port("uplinkA", tagged: new[] { 10, 20, 30 });
                s.Port("uplinkB", tagged: new[] { 10, 20, 30 });

                // Assign this switch's slice of NIC MACs to clean access ports (one MAC per port).
                for (var p = 0; p < accessPortsPerSwitch; p++)
                {
                    var idx = (sw * accessPortsPerSwitch) + p;
                    if (idx < nicMacs.Count)
                    {
                        s.Bridge($"ether{p}", nicMacs[idx]);
                    }
                }

                // Bulk foreign MACs live on the uplinks (trunk), keeping access ports clean.
                for (var f = 0; f < foreignPerUplink; f++)
                {
                    s.Bridge("uplinkA", Mac(0x00AA_0000_0000L + (sw * 100_000) + f));
                    s.Bridge("uplinkB", Mac(0x00BB_0000_0000L + (sw * 100_000) + f));
                }
            });
        }

        for (var srv = 0; srv < serverCount; srv++)
        {
            var serverIndex = srv;
            builder.Server($"srv{serverIndex:D2}", sv =>
            {
                for (var nic = 0; nic < nicsPerServer; nic++)
                {
                    sv.Nic($"eth{nic}", nicMacs[(serverIndex * nicsPerServer) + nic]);
                }
            });
        }

        return builder.Build();
    }
}
