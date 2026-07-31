using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.DesiredState;

/// <summary>
/// Semantic-diff tests for <see cref="SemanticDiffEngine"/> (story #171, AC1): VLAN add/remove/modify and
/// access-port add/remove/change with AC-verbatim summaries, plus determinism (NFR3) — identical inputs
/// yield identical ordered output and stable EntityRef/ChangeId across repeated runs.
/// </summary>
public sealed class SemanticDiffEngineTests
{
    private static readonly Guid RackId = new("33333333-3333-3333-3333-333333333333");

    private static SupportedDesiredStateModel Model(
        IReadOnlyList<VlanCatalogueEntry> vlans, IReadOnlyList<PortAccessIntent> ports)
        => new("rack-a", vlans, ports);

    [Fact]
    public void Detects_a_vlan_add_with_verbatim_summary_and_entity_ref()
    {
        var baseline = Model(Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());
        var candidate = Model(new[] { new VlanCatalogueEntry(100, "web", null) }, Array.Empty<PortAccessIntent>());

        var result = SemanticDiffEngine.Diff(baseline, candidate, RackId);

        result.Changes.Should().ContainSingle();
        var change = result.Changes[0];
        change.Kind.Should().Be(DesiredStateChangeKind.Added);
        change.Category.Should().Be(DesiredStateChangeCategory.Vlan);
        change.Summary.Should().Be("VLAN 100 added");
        change.EntityRef.Should().Be(EntityRef.Vlan(RackId, 100));
        change.After.Should().Contain(f => f.Field == "name" && f.Value == "web");
    }

    [Fact]
    public void Detects_a_vlan_remove_with_verbatim_summary()
    {
        var baseline = Model(new[] { new VlanCatalogueEntry(100, "web", null) }, Array.Empty<PortAccessIntent>());
        var candidate = Model(Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());

        var change = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes.Should().ContainSingle().Subject;

        change.Kind.Should().Be(DesiredStateChangeKind.Removed);
        change.Summary.Should().Be("VLAN 100 removed");
        change.Before.Should().Contain(f => f.Field == "name" && f.Value == "web");
    }

    [Fact]
    public void Detects_a_vlan_name_change_with_verbatim_summary()
    {
        var baseline = Model(new[] { new VlanCatalogueEntry(20, "corp", null) }, Array.Empty<PortAccessIntent>());
        var candidate = Model(new[] { new VlanCatalogueEntry(20, "prod", null) }, Array.Empty<PortAccessIntent>());

        var change = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes.Should().ContainSingle().Subject;

        change.Kind.Should().Be(DesiredStateChangeKind.Modified);
        change.Summary.Should().Be("VLAN 20 name changed 'corp'→'prod'");
    }

    [Fact]
    public void Detects_a_vlan_description_change()
    {
        var baseline = Model(new[] { new VlanCatalogueEntry(20, "corp", "old") }, Array.Empty<PortAccessIntent>());
        var candidate = Model(new[] { new VlanCatalogueEntry(20, "corp", "new") }, Array.Empty<PortAccessIntent>());

        var change = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes.Should().ContainSingle().Subject;

        change.Summary.Should().Be("VLAN 20 description changed 'old'→'new'");
    }

    [Fact]
    public void Detects_an_access_port_vlan_change_with_verbatim_summary()
    {
        var baseline = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether3", 10) });
        var candidate = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether3", 20) });

        var change = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes.Should().ContainSingle().Subject;

        change.Kind.Should().Be(DesiredStateChangeKind.Modified);
        change.Category.Should().Be(DesiredStateChangeCategory.Port);
        change.Summary.Should().Be("Switch sw1 Port ether3 accessVlan changed 10→20");
        change.EntityRef.Should().Be(EntityRef.Port(RackId, "sw1", "ether3"));
    }

    [Fact]
    public void Detects_access_port_add_and_remove()
    {
        var baseline = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether1", 10) });
        var candidate = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether2", 20) });

        var result = SemanticDiffEngine.Diff(baseline, candidate, RackId);

        result.Changes.Should().HaveCount(2);
        result.Changes.Should().Contain(c => c.Kind == DesiredStateChangeKind.Removed && c.EntityRef.PortName == "ether1");
        result.Changes.Should().Contain(c => c.Kind == DesiredStateChangeKind.Added && c.EntityRef.PortName == "ether2");
    }

    [Fact]
    public void A_null_access_vlan_intent_is_treated_as_no_intent()
    {
        var baseline = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether1", null) });
        var candidate = Model(Array.Empty<VlanCatalogueEntry>(), new[] { new PortAccessIntent("sw1", "ether1", null) });

        SemanticDiffEngine.Diff(baseline, candidate, RackId).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Identical_models_yield_an_empty_diff()
    {
        var model = Model(
            new[] { new VlanCatalogueEntry(10, "data", "primary") },
            new[] { new PortAccessIntent("sw1", "ether1", 10) });

        SemanticDiffEngine.Diff(model, model, RackId).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Output_is_ordered_vlans_before_ports_then_by_id_and_ordinal_key()
    {
        var baseline = Model(Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());
        var candidate = Model(
            new[] { new VlanCatalogueEntry(30, "c", null), new VlanCatalogueEntry(10, "a", null) },
            new[] { new PortAccessIntent("sw2", "ether1", 10), new PortAccessIntent("sw1", "ether9", 20) });

        var changes = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes;

        changes.Select(c => c.Summary).Should().ContainInOrder(
            "VLAN 10 added",
            "VLAN 30 added",
            "Switch sw1 Port ether9 accessVlan set to 20",
            "Switch sw2 Port ether1 accessVlan set to 10");
    }

    [Fact]
    public void Repeated_runs_with_identical_inputs_are_byte_identical_including_change_ids()
    {
        var baseline = Model(
            new[] { new VlanCatalogueEntry(20, "corp", null) },
            new[] { new PortAccessIntent("sw1", "ether3", 10) });
        var candidate = Model(
            new[] { new VlanCatalogueEntry(20, "prod", null), new VlanCatalogueEntry(100, "web", null) },
            new[] { new PortAccessIntent("sw1", "ether3", 20) });

        var first = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes;
        var second = SemanticDiffEngine.Diff(baseline, candidate, RackId).Changes;

        first.Select(c => c.ChangeId).Should().Equal(second.Select(c => c.ChangeId));
        first.Select(c => c.Summary).Should().Equal(second.Select(c => c.Summary));
        first.Select(c => c.ChangeId).Should().OnlyHaveUniqueItems();
    }
}
