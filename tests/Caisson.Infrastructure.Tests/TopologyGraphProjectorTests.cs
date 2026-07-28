using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>DB-free tests of the pure graph projector (AC3 read model). No database required.</summary>
public sealed class TopologyGraphProjectorTests
{
    private static readonly Guid RackId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TopologyGraphView Project()
    {
        var snapshot = TopologySnapshotMapper.Map(
            RackId, IngestionTestData.RunContext(), IngestionTestData.Observed(),
            IngestionTestData.Correlation(), new IngestionTestData.SequentialIds().NewId).Snapshot;
        return TopologyGraphProjector.Project(snapshot);
    }

    [Fact]
    public void Projects_confident_nic_to_its_best_attachment_with_band_and_vlans()
    {
        var view = Project();
        var server1 = view.Servers.Single(s => s.BmcUuid == "uuid-1");
        var nic = server1.Nics.Single();

        nic.BestAttachment.Should().NotBeNull();
        nic.BestAttachment!.PortName.Should().Be("ether1");
        nic.BestAttachment.Band.Should().Be("High");
        nic.BestAttachment.Confidence.Should().BeApproximately(0.92, 1e-9);
        nic.BestAttachment.Vlans.Should().Contain(10);
        nic.BestAttachment.SwitchStableKey.Should().Be("SW-1");
    }

    [Fact]
    public void Ambiguous_nic_exposes_all_candidates_best_first()
    {
        var view = Project();
        var nic = view.Servers.Single(s => s.BmcUuid == "uuid-2").Nics.Single(n => n.Name == "eth0");

        nic.Candidates.Should().HaveCount(2);
        nic.Candidates.Select(c => c.Confidence).Should().BeInDescendingOrder();
        nic.BestAttachment!.PortName.Should().Be("ether2"); // highest confidence
    }

    [Fact]
    public void Unmapped_nic_has_no_attachment()
    {
        var view = Project();
        var nic = view.Servers.Single(s => s.BmcUuid == "uuid-2").Nics.Single(n => n.Name == "eth1");

        nic.BestAttachment.Should().BeNull();
        nic.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Unmapped_port_is_surfaced_by_the_anti_join()
    {
        var view = Project();

        view.UnmappedPorts.Should().ContainSingle(p => p.PortName == "ether4");
        view.UnmappedPorts.Should().NotContain(p => p.PortName == "ether1");
    }
}
