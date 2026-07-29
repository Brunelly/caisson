using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Drift.Tests;

/// <summary>One scenario per <see cref="DriftType"/> rule (story #64, AC1/AC2), plus the natural-key join.</summary>
public sealed class DriftEngineTests
{
    private static readonly DriftComputationOptions Options = new();

    [Fact]
    public void Matched_port_with_no_differences_produces_no_drift()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10);
        var observed = DriftFixtures.Observed(rackId, pvid: 10);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().BeEmpty();
        result.HasAmbiguities.Should().BeFalse();
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void Desired_port_absent_from_observed_produces_MissingDesiredEntity()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired();
        var observed = DriftFixtures.Observed(rackId, includePort: false);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.MissingDesiredEntity);
        item.Severity.Should().Be(DriftSeverity.High);
        item.Actionable.Should().BeTrue();
        item.SubjectType.Should().Be(DriftSubjectType.SwitchPort);
        item.ExpectedValue.Should().Be("10");
        item.ActualValue.Should().BeNull();
    }

    [Fact]
    public void Observed_port_absent_from_desired_produces_ExtraObservedEntity()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.Observed(rackId, pvid: 20);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.ExtraObservedEntity);
        item.Severity.Should().Be(DriftSeverity.Low);
        item.Actionable.Should().BeTrue();
        item.ExpectedValue.Should().BeNull();
        item.ActualValue.Should().Be("20");
    }

    [Fact]
    public void Mismatched_access_vlan_produces_AccessVlanMismatch()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10);
        var observed = DriftFixtures.Observed(rackId, pvid: 20);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle(i => i.DriftType == DriftType.AccessVlanMismatch);
        var item = result.Items.Single(i => i.DriftType == DriftType.AccessVlanMismatch);
        item.Severity.Should().Be(DriftSeverity.High);
        item.Actionable.Should().BeTrue();
        item.ExpectedValue.Should().Be("10");
        item.ActualValue.Should().Be("20");
    }

    [Fact]
    public void Tagged_vlans_on_an_observed_port_produce_UnexpectedTrunkConfig()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10);
        var observed = DriftFixtures.Observed(rackId, pvid: 10, taggedVlans: new[] { 30, 40 });

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.UnexpectedTrunkConfig);
        item.Severity.Should().Be(DriftSeverity.Medium);
        item.ActualValue.Should().Be("30,40");
    }

    [Fact]
    public void A_trunk_all_vlans_port_produces_a_bounded_UnexpectedTrunkConfig_item_instead_of_throwing()
    {
        // A legitimate "trunk all VLANs" uplink can carry thousands of tagged VLANs — device-controlled
        // volume that must degrade to a single, bounded item (M1 invariant) rather than making the
        // DriftItem constructor throw and fail the whole rack's report.
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10);
        var allVlans = Enumerable.Range(1, 4094).ToArray();
        var observed = DriftFixtures.Observed(rackId, pvid: 10, taggedVlans: allVlans);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.UnexpectedTrunkConfig);
        item.ActualValue.Should().NotBeNull();
        item.ActualValue!.Length.Should().BeLessThan(DriftSchema.MaxActualValueLength);
        item.ActualValue.Should().Contain("more");
        item.ActualValue.Should().Contain("4094 total");
    }

    [Fact]
    public void Many_observed_LLDP_neighbours_produce_a_bounded_UnexpectedNeighbour_item_instead_of_throwing()
    {
        // Same device-controlled-volume concern as the trunk-VLAN case above, for LLDP neighbours: a
        // port with many (e.g. misbehaving/garbage) LLDP neighbours must still degrade to one bounded
        // item rather than throw.
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10, neighborSystemName: "expected-neighbour");
        var observed = DriftFixtures.Observed(rackId, pvid: 10);

        var port = observed.Switches.Single().Ports.Single();
        for (var i = 0; i < 200; i++)
        {
            port.AddLldpNeighbour(new LldpNeighbour(
                Guid.NewGuid(), port.Id, rackId, observed.Id, $"chassis-{i}", $"remote-port-{i}", $"unexpected-host-{i}"));
        }

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle(i => i.DriftType == DriftType.UnexpectedNeighbour);
        var item = result.Items.Single(i => i.DriftType == DriftType.UnexpectedNeighbour);
        item.ActualValue.Should().NotBeNull();
        item.ActualValue!.Length.Should().BeLessThan(DriftSchema.MaxActualValueLength);
        item.ActualValue.Should().Contain("more");
    }

    [Fact]
    public void Declared_neighbor_not_observed_produces_UnexpectedNeighbour()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10, neighborSystemName: "server-42");
        var observed = DriftFixtures.Observed(rackId, pvid: 10, neighborSystemName: "some-other-host");

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.UnexpectedNeighbour);
        item.Severity.Should().Be(DriftSeverity.Medium);
        item.Actionable.Should().BeTrue();
    }

    [Fact]
    public void Declared_neighbor_that_matches_observed_produces_no_neighbour_drift()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10, neighborSystemName: "server-42", neighborPortId: "eth0");
        var observed = DriftFixtures.Observed(rackId, pvid: 10, neighborSystemName: "server-42", neighborPortId: "eth0");

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_nic_produces_a_single_nonactionable_UnknownTopologyMapping_item()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.ObservedWithAmbiguousNic(rackId);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        // The two candidate ports must NOT also produce ExtraObservedEntity port-level items — the NIC
        // ambiguity is the only item (AC2: never imply a specific change on an uncertain subject).
        result.Items.Should().HaveCount(3); // 1 ambiguity item + 2 ExtraObservedEntity for the two real (unrelated) ports
        var ambiguity = result.Items.Should().ContainSingle(i => i.DriftType == DriftType.UnknownTopologyMapping).Subject;
        ambiguity.Actionable.Should().BeFalse();
        ambiguity.SubjectType.Should().Be(DriftSubjectType.ServerNic);
        ambiguity.DetailsJson.Should().Contain("candidatePorts");
        result.HasAmbiguities.Should().BeTrue();
    }

    [Fact]
    public void Unmapped_nic_with_zero_candidates_produces_UnknownTopologyMapping()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.ObservedWithUnmappedNic(rackId);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().ContainSingle();
        var item = result.Items[0];
        item.DriftType.Should().Be(DriftType.UnknownTopologyMapping);
        item.Actionable.Should().BeFalse();
    }

    [Fact]
    public void Nic_with_a_single_uncontested_candidate_produces_no_drift()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.ObservedWithCleanNic(rackId);

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        // The NIC's own candidate port ("ether3") is an unrelated, real observed port with no desired
        // counterpart in this fixture — it legitimately produces its own ExtraObservedEntity item, but
        // no UnknownTopologyMapping item is produced for the NIC itself (it is not ambiguous).
        result.Items.Should().ContainSingle();
        result.Items[0].DriftType.Should().Be(DriftType.ExtraObservedEntity);
        result.HasAmbiguities.Should().BeFalse();
    }

    [Fact]
    public void Natural_key_join_matches_on_switch_name_and_port_name_not_stable_key()
    {
        // Desired and observed StableKey columns are never string-comparable across the boundary (ADR
        // 0029); the join must still succeed purely via SwitchName/PortName equality.
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 10, switchName: "core-switch-1", portName: "1/1/1");
        var observed = DriftFixtures.Observed(rackId, pvid: 10, switchName: "core-switch-1", portName: "1/1/1");

        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, Options);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void Items_are_capped_at_MaxItemsPerReport_after_canonical_sort_and_marks_truncated()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.Observed(rackId, includePort: false);

        // Force several ExtraObservedEntity items by adding many ports directly to the fixture's switch.
        var sw = observed.Switches.Single();
        for (var i = 0; i < 5; i++)
        {
            sw.AddPort(new Caisson.Domain.Topology.SwitchPort(Guid.NewGuid(), sw.Id, rackId, observed.Id, $"ether{i}"));
        }

        var options = new DriftComputationOptions { MaxItemsPerReport = 2 };
        var result = DriftEngine.Compute(desired, observed, rackId, DateTime.UtcNow, options);

        result.Items.Should().HaveCount(2);
        result.IsTruncated.Should().BeTrue();
        result.Items.Should().BeInAscendingOrder(i => i.SubjectKey, StringComparer.Ordinal);
    }
}
