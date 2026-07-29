using Caisson.Domain.Topology.Diffing;
using Caisson.Ingestion.Materializer;
using Caisson.Ingestion.Schema;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>Story #62, AC3: typed rack/switch/port intents with stable identifiers, materialised from a validated document.</summary>
public sealed class DesiredStateMaterializerTests
{
    [Fact]
    public void Materialize_produces_typed_intents_with_stable_identifiers()
    {
        var document = new ValidatedRackDocument(
            "rack-1",
            new[]
            {
                new ValidatedSwitch(
                    "switch-a",
                    new[]
                    {
                        new ValidatedPort("eth0", 100, "uplink", "leaf-1", "Ethernet1"),
                        new ValidatedPort("eth1", 200, null, null, null),
                    }),
            });

        var versionId = Guid.NewGuid();
        var ids = new Queue<Guid>(Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()));
        var result = DesiredStateMaterializer.Materialize(versionId, document, () => ids.Dequeue());

        result.Rack.RackSlug.Should().Be("rack-1");
        result.Rack.DesiredStateVersionId.Should().Be(versionId);
        result.Rack.StableKey.Should().Be("rack-1");

        result.Switches.Should().ContainSingle();
        var switchIntent = result.Switches[0];
        switchIntent.SwitchName.Should().Be("switch-a");
        switchIntent.DesiredRackIntentId.Should().Be(result.Rack.Id);
        switchIntent.StableKey.Should().Be("rack-1|switch-a");

        result.Ports.Should().HaveCount(2);
        var expectedKey = StableKeys.ForSwitchPort(switchIntent.StableKey, "eth0");
        var eth0 = result.Ports.Single(p => p.PortName == "eth0");
        eth0.DesiredSwitchIntentId.Should().Be(switchIntent.Id);
        eth0.StableKey.Should().Be(expectedKey);
        eth0.AccessVlan.Should().Be(100);
        eth0.Description.Should().Be("uplink");
        eth0.NeighborSystemName.Should().Be("leaf-1");
        eth0.NeighborPortId.Should().Be("Ethernet1");

        var eth1 = result.Ports.Single(p => p.PortName == "eth1");
        eth1.Description.Should().BeNull();
        eth1.NeighborSystemName.Should().BeNull();
    }

    [Fact]
    public void Materialize_is_pure_and_deterministic_for_the_same_id_sequence()
    {
        var document = new ValidatedRackDocument(
            "rack-2", new[] { new ValidatedSwitch("sw", new[] { new ValidatedPort("p0", 10, null, null, null) }) });

        var fixedIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        MaterializedRackIntent Run()
        {
            var queue = new Queue<Guid>(fixedIds);
            return DesiredStateMaterializer.Materialize(Guid.Empty, document, () => queue.Dequeue());
        }

        var first = Run();
        var second = Run();

        first.Rack.Id.Should().Be(second.Rack.Id);
        first.Switches[0].StableKey.Should().Be(second.Switches[0].StableKey);
        first.Ports[0].StableKey.Should().Be(second.Ports[0].StableKey);
    }
}
