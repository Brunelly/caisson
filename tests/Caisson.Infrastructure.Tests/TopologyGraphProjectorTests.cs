using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
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
        // Both ambiguous candidates sit in the Medium band (0.60, 0.55) — exercises the Medium band.
        nic.Candidates.Should().OnlyContain(c => c.Band == "Medium");
    }

    [Theory]
    [InlineData(0.80, "High")]   // inclusive lower bound of High
    [InlineData(0.79, "Medium")] // just below High
    [InlineData(0.50, "Medium")] // inclusive lower bound of Medium
    [InlineData(0.49, "Low")]    // just below Medium
    [InlineData(0.00, "Low")]
    public void Projects_the_confidence_band_at_each_boundary(double confidence, string expectedBand)
    {
        var view = TopologyGraphProjector.Project(SnapshotWithSingleCandidate(confidence));

        var attachment = view.Servers.Single().Nics.Single().BestAttachment;
        attachment.Should().NotBeNull();
        attachment!.Band.Should().Be(expectedBand);
        attachment.Confidence.Should().BeApproximately(confidence, 1e-9);
    }

    // Builds the smallest snapshot the projector can shape: one switch/port, one server/NIC, and a single
    // candidate mapping at the given confidence — isolating the band classification the projector applies.
    private static TopologySnapshot SnapshotWithSingleCandidate(double confidence)
    {
        var rackId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snapshot = new TopologySnapshot(
            snapshotId, rackId, DateTime.UtcNow, "svc", "chr", Guid.NewGuid(), SnapshotStatus.Completed);

        var sw = new Switch(Guid.NewGuid(), rackId, snapshotId, DateTime.UtcNow, externalDeviceKey: "sw-9", serial: "SW-9");
        var port = new SwitchPort(Guid.NewGuid(), sw.Id, rackId, snapshotId, "ether1", isUp: true, pvid: 10);
        sw.AddPort(port);
        snapshot.AddSwitch(sw);

        var server = new Server(Guid.NewGuid(), rackId, snapshotId, BmcType.Redfish, "10.0.1.9", "srv-9", "uuid-9", "node-9");
        var nic = new Nic(
            Guid.NewGuid(), server.Id, rackId, snapshotId, "eth0", MacAddressValue.Parse("aa:aa:aa:aa:aa:b1"));
        server.AddNic(nic);
        snapshot.AddServer(server);

        snapshot.AddCandidateMapping(new TopologyCandidateMapping(
            Guid.NewGuid(), rackId, snapshotId, nic.Id, ConfidenceScore.From(confidence),
            ReasonCode.MacLearnUnique, port.Id));

        return snapshot;
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
    public void Unmapped_nic_surfaces_its_reason_code_instead_of_being_dropped()
    {
        var view = Project();
        var nic = view.Servers.Single(s => s.BmcUuid == "uuid-2").Nics.Single(n => n.Name == "eth1");

        nic.UnmappedReasonCode.Should().Be(ReasonCode.NotSeenInSwitch.ToString());
    }

    [Fact]
    public void Mapped_nic_has_no_unmapped_reason_code()
    {
        var view = Project();
        var server1 = view.Servers.Single(s => s.BmcUuid == "uuid-1");
        var nic = server1.Nics.Single();

        nic.UnmappedReasonCode.Should().BeNull();
    }

    [Fact]
    public void Unmapped_port_is_surfaced_by_the_anti_join()
    {
        var view = Project();

        view.UnmappedPorts.Should().ContainSingle(p => p.PortName == "ether4");
        view.UnmappedPorts.Should().NotContain(p => p.PortName == "ether1");
    }
}
