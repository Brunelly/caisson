using Caisson.Domain.NetworkConfig;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.NetworkConfig;

/// <summary>
/// The single shared network-intent validation ruleset (story #168, AC1/AC2, NFR5) — exercised directly
/// here since both the PUT save path and the <c>/validate</c> stub call the exact same method.
/// </summary>
public sealed class NetworkIntentValidatorTests
{
    [Fact]
    public void A_valid_catalogue_and_port_intents_produce_no_errors()
    {
        var catalogue = new[] { new VlanCatalogueEntry(120, "storage", "iSCSI") };
        var portIntents = new[] { new PortAccessIntent("SW-1", "ether1", 120) };

        var errors = NetworkIntentValidator.Validate(catalogue, portIntents);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Empty_catalogue_and_port_intents_are_valid()
        => NetworkIntentValidator.Validate(
            Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>()).Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    [InlineData(-1)]
    public void Out_of_range_vlan_ids_are_rejected(int vlanId)
    {
        var catalogue = new[] { new VlanCatalogueEntry(vlanId, "storage", null) };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[0].id");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4094)]
    public void Boundary_vlan_ids_are_accepted(int vlanId)
    {
        var catalogue = new[] { new VlanCatalogueEntry(vlanId, "storage", null) };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_vlan_ids_within_a_catalogue_are_rejected()
    {
        var catalogue = new[]
        {
            new VlanCatalogueEntry(120, "storage", null),
            new VlanCatalogueEntry(120, "storage-2", null),
        };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[1].id" && e.Message.Contains("already exists"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_vlan_name_is_rejected(string name)
    {
        var catalogue = new[] { new VlanCatalogueEntry(120, name, null) };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[0].name");
    }

    [Fact]
    public void Vlan_name_over_the_bound_is_rejected()
    {
        var oversized = new string('a', NetworkIntentValidator.MaxVlanNameLength + 1);
        var catalogue = new[] { new VlanCatalogueEntry(120, oversized, null) };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[0].name");
    }

    [Fact]
    public void Description_over_the_bound_is_rejected()
    {
        var oversized = new string('a', Caisson.Domain.DesiredState.DesiredStateSchema.MaxDescriptionLength + 1);
        var catalogue = new[] { new VlanCatalogueEntry(120, "storage", oversized) };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[0].description");
    }

    [Fact]
    public void Null_description_is_valid()
    {
        var catalogue = new[] { new VlanCatalogueEntry(120, "storage", null) };

        NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>()).Should().BeEmpty();
    }

    [Fact]
    public void Port_intent_referencing_a_vlan_not_in_the_catalogue_is_rejected()
    {
        var catalogue = new[] { new VlanCatalogueEntry(120, "storage", null) };
        var portIntents = new[] { new PortAccessIntent("SW-1", "ether1", 999) };

        var errors = NetworkIntentValidator.Validate(catalogue, portIntents);

        errors.Should().ContainSingle(e =>
            e.Field == "portIntents[0].accessVlanId" && e.Message.Contains("999"));
    }

    [Fact]
    public void Port_intent_with_a_null_access_vlan_id_unchanged_inherit_is_always_valid()
    {
        var portIntents = new[] { new PortAccessIntent("SW-1", "ether1", null) };

        NetworkIntentValidator.Validate(Array.Empty<VlanCatalogueEntry>(), portIntents).Should().BeEmpty();
    }

    /// <summary>
    /// Story Q2's "block deletion of a VLAN still referenced by a port intent" falls out of the same
    /// catalogue-membership check above: a PUT payload always carries the FULL catalogue, so removing a
    /// still-referenced VLAN entry (rather than merely never adding it) surfaces via the identical
    /// "VLAN does not exist in this rack's catalogue" error.
    /// </summary>
    [Fact]
    public void Removing_a_vlan_still_referenced_by_a_port_intent_is_rejected_as_an_unknown_vlan_reference()
    {
        var catalogueWithoutTheReferencedVlan = Array.Empty<VlanCatalogueEntry>();
        var portIntents = new[] { new PortAccessIntent("SW-1", "ether1", 120) };

        var errors = NetworkIntentValidator.Validate(catalogueWithoutTheReferencedVlan, portIntents);

        errors.Should().ContainSingle(e => e.Field == "portIntents[0].accessVlanId");
    }

    [Fact]
    public void Missing_switch_stable_key_or_port_name_is_rejected()
    {
        var portIntents = new[] { new PortAccessIntent("", "", null) };

        var errors = NetworkIntentValidator.Validate(Array.Empty<VlanCatalogueEntry>(), portIntents);

        errors.Should().Contain(e => e.Field == "portIntents[0].switchStableKey");
        errors.Should().Contain(e => e.Field == "portIntents[0].portName");
    }

    [Fact]
    public void Every_problem_is_accumulated_rather_than_stopping_at_the_first()
    {
        var catalogue = new[]
        {
            new VlanCatalogueEntry(0, "", new string('a', 500)),
        };

        var errors = NetworkIntentValidator.Validate(catalogue, Array.Empty<PortAccessIntent>());

        errors.Should().HaveCount(3);
    }
}
