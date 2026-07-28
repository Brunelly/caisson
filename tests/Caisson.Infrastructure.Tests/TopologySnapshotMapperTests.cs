using System.Text.Json;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology.Diffing;
using Caisson.Infrastructure.Persistence.Ingestion;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of the correlation-result → domain mapper (AC: mapping). No database required, so
/// these always run in the codegen sandbox.
/// </summary>
public sealed class TopologySnapshotMapperTests
{
    private static readonly Guid RackId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MappedSnapshot Map()
        => TopologySnapshotMapper.Map(
            RackId, IngestionTestData.RunContext(), IngestionTestData.Observed(),
            IngestionTestData.Correlation(), new IngestionTestData.SequentialIds().NewId);

    [Fact]
    public void Builds_the_full_switch_server_vlan_graph_with_run_metadata()
    {
        var snapshot = Map().Snapshot;

        snapshot.RackId.Should().Be(RackId);
        snapshot.Version.Should().Be(1);
        snapshot.TriggerType.Should().Be(TriggerType.OnDemand);
        snapshot.Switches.Should().HaveCount(1);
        snapshot.Switches.Single().Ports.Should().HaveCount(4);
        snapshot.Servers.Should().HaveCount(2);
        snapshot.Vlans.Should().HaveCount(2); // deduped per rack by vlan id
    }

    [Fact]
    public void Maps_bmc_and_switch_macs_linking_known_nics()
    {
        var mapped = Map();

        // 2 BMC macs (srv1/eth0, srv2/eth0, srv2/eth1) + 2 switch bridge macs.
        var bmc = mapped.MacAddresses.Where(m => m.Source == MacSource.Bmc).ToList();
        var switched = mapped.MacAddresses.Where(m => m.Source == MacSource.Switch).ToList();

        bmc.Should().HaveCount(3); // eth0(A), eth0(B), eth1(C) — the MAC-less eth2 is skipped
        switched.Should().HaveCount(2);

        // Switch-learned MacA matches srv1/eth0's NIC and links to it; the others link where known.
        switched.Should().Contain(m => m.NicId != null); // MacA / MacB both match a known NIC
    }

    [Fact]
    public void Confident_mapping_becomes_a_single_candidate_with_switch_port_and_primary_reason()
    {
        var snapshot = Map().Snapshot;
        var nicA = snapshot.Servers.Single(s => s.BmcUuid == "uuid-1").Nics.Single();

        var candidates = snapshot.CandidateMappings.Where(c => c.NicId == nicA.Id).ToList();
        candidates.Should().HaveCount(1);
        candidates[0].SwitchPortId.Should().NotBeNull();
        candidates[0].Confidence.Value.Should().BeApproximately(0.92, 1e-9);
        candidates[0].ReasonCode.Should().Be(ReasonCode.MacLearnUnique); // ReasonCodes[0]

        using var doc = JsonDocument.Parse(candidates[0].EvidenceJson!);
        doc.RootElement.GetProperty("band").GetString().Should().Be("High");
        doc.RootElement.GetProperty("reasonCodes").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("vlans")[0].GetInt32().Should().Be(10);
    }

    [Fact]
    public void Ambiguous_mapping_becomes_ordered_candidates()
    {
        var snapshot = Map().Snapshot;
        var nicB = snapshot.Servers.Single(s => s.BmcUuid == "uuid-2").Nics.Single(n => n.Name == "eth0");

        var candidates = snapshot.CandidateMappings
            .Where(c => c.NicId == nicB.Id)
            .ToList();

        candidates.Should().HaveCount(2);
        candidates.Select(c => c.Confidence.Value).Should().BeInDescendingOrder();
        candidates.Should().OnlyContain(c => c.SwitchPortId != null);
    }

    [Fact]
    public void Unmapped_nic_becomes_a_candidate_with_null_switch_port()
    {
        var snapshot = Map().Snapshot;
        var nicC = snapshot.Servers.Single(s => s.BmcUuid == "uuid-2").Nics.Single(n => n.Name == "eth1");

        var candidate = snapshot.CandidateMappings.Single(c => c.NicId == nicC.Id);
        candidate.SwitchPortId.Should().BeNull();
        candidate.ReasonCode.Should().Be(ReasonCode.NotSeenInSwitch);
        candidate.Confidence.Value.Should().Be(0.0);
    }

    [Fact]
    public void Unmapped_port_is_a_plain_switch_port_with_no_candidate()
    {
        var snapshot = Map().Snapshot;
        var ether4 = snapshot.Switches.Single().Ports.Single(p => p.PortName == "ether4");

        snapshot.CandidateMappings.Should().NotContain(c => c.SwitchPortId == ether4.Id);
    }

    [Fact]
    public void Mac_less_nic_is_skipped_and_produces_no_nic()
    {
        var snapshot = Map().Snapshot;
        var srv2 = snapshot.Servers.Single(s => s.BmcUuid == "uuid-2");

        srv2.Nics.Should().NotContain(n => n.Name == "eth2");
    }

    [Fact]
    public void Switch_stable_key_falls_back_to_serial()
    {
        var snapshot = Map().Snapshot;
        StableKeys.ForSwitch(snapshot.Switches.Single()).Should().Be("SW-1");
    }
}
