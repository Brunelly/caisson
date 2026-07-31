using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.NetworkConfig.Preflight;

/// <summary>
/// Per-rule tests for the pure pre-flight validation engine (story #170): schema/semantic reuse of
/// <see cref="NetworkIntentValidator"/>, topology resolution, safety guardrails, deterministic ordering,
/// and JSON-Pointer field paths.
/// </summary>
public sealed class PreflightValidatorTests
{
    private static readonly Guid RackId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SnapshotId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_valid_candidate_against_known_topology_produces_no_issues()
    {
        var catalogue = new[] { new VlanCatalogueEntry(10, "data", null) };
        var intents = new[] { new PortAccessIntent("sw1", "ether1", 10) };

        var issues = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);

        issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    public void Out_of_range_vlan_id_is_a_schema_error(int vlanId)
    {
        var catalogue = new[] { new VlanCatalogueEntry(vlanId, "data", null) };

        var issues = PreflightValidator.Validate(catalogue, NoIntents, Inventory(), RackId);

        var issue = issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be(PreflightCodes.VlanIdRange);
        issue.Severity.Should().Be(PreflightSeverity.Error);
        issue.FieldPath.Should().Be("/vlanCatalogue/0/id");
        issue.UiPath.Should().Be("vlanCatalogue.vlans[0].id");
        issue.EntityRef.Kind.Should().Be(EntityKind.Vlan);
    }

    [Fact]
    public void Duplicate_vlan_ids_report_each_conflicting_entry_with_the_duplicate_code()
    {
        var catalogue = new[]
        {
            new VlanCatalogueEntry(10, "a", null),
            new VlanCatalogueEntry(10, "b", null),
            new VlanCatalogueEntry(10, "c", null),
        };

        var issues = PreflightValidator.Validate(catalogue, NoIntents, Inventory(), RackId);

        issues.Should().OnlyContain(i => i.Code == PreflightCodes.DuplicateVlanId);
        issues.Select(i => i.FieldPath).Should()
            .BeEquivalentTo(new[] { "/vlanCatalogue/1/id", "/vlanCatalogue/2/id" });
    }

    [Fact]
    public void Missing_and_over_long_vlan_names_map_to_distinct_schema_codes()
    {
        var longName = new string('x', 65);
        var catalogue = new[]
        {
            new VlanCatalogueEntry(10, "  ", null),
            new VlanCatalogueEntry(11, longName, null),
        };

        var issues = PreflightValidator.Validate(catalogue, NoIntents, Inventory(), RackId);

        issues.Should().Contain(i => i.Code == PreflightCodes.VlanNameRequired && i.FieldPath == "/vlanCatalogue/0/name");
        issues.Should().Contain(i => i.Code == PreflightCodes.VlanNameLength && i.FieldPath == "/vlanCatalogue/1/name");
    }

    [Fact]
    public void A_port_intent_referencing_an_absent_vlan_is_a_semantic_error()
    {
        var intents = new[] { new PortAccessIntent("sw1", "ether1", 999) };

        var issues = PreflightValidator.Validate(NoCatalogue, intents, Inventory(), RackId);

        issues.Should().ContainSingle(i => i.Code == PreflightCodes.VlanNotInCatalogue
            && i.FieldPath == "/portIntents/0/accessVlanId");
    }

    [Fact]
    public void An_unknown_switch_and_an_unknown_port_are_reported_independently()
    {
        var catalogue = new[] { new VlanCatalogueEntry(10, "data", null) };
        var unknownSwitch = new[] { new PortAccessIntent("nope", "ether1", 10) };
        var unknownPort = new[] { new PortAccessIntent("sw1", "ether99", 10) };

        PreflightValidator.Validate(catalogue, unknownSwitch, Inventory(), RackId)
            .Should().ContainSingle(i => i.Code == PreflightCodes.SwitchNotFound
                && i.FieldPath == "/portIntents/0/switchStableKey"
                && i.Message.Contains("refresh topology"));

        PreflightValidator.Validate(catalogue, unknownPort, Inventory(), RackId)
            .Should().ContainSingle(i => i.Code == PreflightCodes.PortNotFound
                && i.FieldPath == "/portIntents/0/portName"
                && i.Message.Contains("select a known port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_empty_inventory_is_a_blocking_topology_error_not_a_switch_not_found_storm()
    {
        var catalogue = new[] { new VlanCatalogueEntry(10, "data", null) };
        var intents = new[]
        {
            new PortAccessIntent("sw1", "ether1", 10),
            new PortAccessIntent("sw1", "ether2", 10),
        };

        var issues = PreflightValidator.Validate(catalogue, intents, RackInventory.Empty(RackId), RackId);

        issues.Should().ContainSingle(i => i.Code == PreflightCodes.TopologyUnavailable);
        issues.Should().OnlyContain(i => i.Severity == PreflightSeverity.Error);
        issues.Single(i => i.Code == PreflightCodes.TopologyUnavailable).EntityRef.Kind.Should().Be(EntityKind.Rack);
    }

    [Fact]
    public void Identical_duplicate_port_intents_use_a_different_code_than_a_vlan_conflict()
    {
        var catalogue = new[] { new VlanCatalogueEntry(10, "a", null), new VlanCatalogueEntry(20, "b", null) };

        var identical = new[]
        {
            new PortAccessIntent("sw1", "ether1", 10),
            new PortAccessIntent("sw1", "ether1", 10),
        };
        PreflightValidator.Validate(catalogue, identical, Inventory(), RackId)
            .Should().ContainSingle(i => i.Code == PreflightCodes.DuplicatePortIntent
                && i.FieldPath == "/portIntents/1/portName");

        var conflicting = new[]
        {
            new PortAccessIntent("sw1", "ether1", 10),
            new PortAccessIntent("sw1", "ether1", 20),
        };
        PreflightValidator.Validate(catalogue, conflicting, Inventory(), RackId)
            .Should().ContainSingle(i => i.Code == PreflightCodes.PortVlanConflict
                && i.FieldPath == "/portIntents/1/accessVlanId");
    }

    [Fact]
    public void A_change_to_an_uplink_port_is_a_non_blocking_heuristic_safety_warning()
    {
        var catalogue = new[] { new VlanCatalogueEntry(30, "x", null) };
        var intents = new[] { new PortAccessIntent("sw1", "ether2", 30) }; // ether2 is Uplink, Pvid 20.

        var issues = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);

        var warning = issues.Should().ContainSingle(i => i.Code == PreflightCodes.UplinkPort).Subject;
        warning.Severity.Should().Be(PreflightSeverity.Warning);
        warning.FieldPath.Should().Be("/portIntents/0/accessVlanId");
        warning.Details.Should().ContainKey("reason").WhoseValue.Should().Be("heuristic-derived");
        warning.EntityRef.Should().BeEquivalentTo(EntityRef.Port(RackId, "sw1", "ether2"));
    }

    [Fact]
    public void A_change_to_a_management_port_warns_about_severing_the_management_path()
    {
        var catalogue = new[] { new VlanCatalogueEntry(30, "x", null) };
        var intents = new[] { new PortAccessIntent("sw1", "mgmt", 30) };

        var issues = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);

        issues.Should().ContainSingle(i => i.Code == PreflightCodes.ManagementPort
            && i.Severity == PreflightSeverity.Warning
            && i.Message.Contains("management path"));
    }

    [Fact]
    public void An_assignment_matching_the_observed_native_vlan_is_not_a_change_and_warns_nothing()
    {
        var catalogue = new[] { new VlanCatalogueEntry(20, "x", null) };
        var intents = new[] { new PortAccessIntent("sw1", "ether2", 20) }; // ether2 Pvid is 20 — no change.

        PreflightValidator.Validate(catalogue, intents, Inventory(), RackId).Should().BeEmpty();
    }

    [Fact]
    public void Safety_warnings_are_suppressed_while_any_blocking_error_exists()
    {
        // A schema error (bad VLAN) plus a change to the uplink port: no safety warning until errors clear.
        var catalogue = new[] { new VlanCatalogueEntry(5000, "bad", null), new VlanCatalogueEntry(30, "x", null) };
        var intents = new[] { new PortAccessIntent("sw1", "ether2", 30) };

        var issues = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);

        issues.Should().Contain(i => i.Severity == PreflightSeverity.Error);
        issues.Should().NotContain(i => i.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public void The_issue_set_and_field_paths_are_deterministic_across_runs()
    {
        var catalogue = new[] { new VlanCatalogueEntry(5000, "bad", null), new VlanCatalogueEntry(10, "data", null) };
        var intents = new[]
        {
            new PortAccessIntent("sw1", "ether99", 10),
            new PortAccessIntent("nope", "ether1", 10),
        };

        var first = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);
        var second = PreflightValidator.Validate(catalogue, intents, Inventory(), RackId);

        first.Select(i => (i.Code, i.FieldPath)).Should()
            .Equal(second.Select(i => (i.Code, i.FieldPath)));
    }

    private static readonly VlanCatalogueEntry[] NoCatalogue = Array.Empty<VlanCatalogueEntry>();
    private static readonly PortAccessIntent[] NoIntents = Array.Empty<PortAccessIntent>();

    /// <summary>A rack with one switch "sw1": an access port, an uplink port and a management port.</summary>
    private static RackInventory Inventory()
        => new(RackId, SnapshotId, new[]
        {
            new InventorySwitch("sw1", new[]
            {
                Port("ether1", pvid: 10, PortRole.Access, null),
                Port("ether2", pvid: 20, PortRole.Uplink, "LLDP neighbour is another switch"),
                Port("mgmt", pvid: 99, PortRole.Management, "reserved management port name"),
            }),
        });

    private static InventoryPort Port(string name, int pvid, PortRole role, string? reason)
        => new($"sw1|{name}", name, Array.Empty<int>(), pvid, true,
            Array.Empty<InventoryLldpNeighbour>(), role, reason);
}
