using System.Diagnostics;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.RoundTrip;

/// <summary>
/// Story #169, NFR2: import and export must be performant for a reference rack (up to 2 switches, 48 ports
/// each, 200 VLANs). The dev-machine budget is P95 &lt; 500ms; this asserts against a generous CI ceiling so
/// the guard is meaningful without being flaky on a loaded shared runner.
/// </summary>
public sealed class DesiredStateRoundTripPerformanceTests
{
    private const int VlanCount = 200;
    private const int SwitchCount = 2;
    private const int PortsPerSwitch = 48;
    private static readonly TimeSpan CiCeiling = TimeSpan.FromSeconds(2);

    private static SupportedDesiredStateModel ReferenceRack()
    {
        var vlans = new List<VlanCatalogueEntry>(VlanCount);
        for (var i = 0; i < VlanCount; i++)
        {
            vlans.Add(new VlanCatalogueEntry(i + 1, $"vlan{i + 1}", $"description for vlan {i + 1}"));
        }

        var ports = new List<PortAccessIntent>(SwitchCount * PortsPerSwitch);
        for (var s = 0; s < SwitchCount; s++)
        {
            for (var p = 0; p < PortsPerSwitch; p++)
            {
                ports.Add(new PortAccessIntent($"sw{s + 1}", $"eth{p + 1}", (p % VlanCount) + 1));
            }
        }

        return new SupportedDesiredStateModel("rack-perf", vlans, ports);
    }

    [Fact]
    public void Reference_rack_renders_and_imports_well_under_budget()
    {
        var model = ReferenceRack();

        // Warm up JIT/regex so the timed run reflects steady state.
        var warm = DesiredStateYamlRenderer.Render(model).Yaml;
        DesiredStateYamlImporter.Import(warm);

        var stopwatch = Stopwatch.StartNew();
        var yaml = DesiredStateYamlRenderer.Render(model).Yaml;
        var imported = DesiredStateYamlImporter.Import(yaml);
        stopwatch.Stop();

        imported.IsSuccess.Should().BeTrue();
        imported.Envelope!.SupportedModel.VlanCatalogue.Should().HaveCount(VlanCount);
        imported.Envelope.SupportedModel.PortIntents.Should().HaveCount(SwitchCount * PortsPerSwitch);
        stopwatch.Elapsed.Should().BeLessThan(CiCeiling);
    }
}
