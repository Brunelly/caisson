using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>AC5: VLAN association is derived and explained when evidence exists, else unknown with a reason.</summary>
public sealed class VlanAssociationTests
{
    private static readonly ITopologyCorrelationEngine Engine = new TopologyCorrelationEngine();

    [Fact]
    public void Vlan_present_is_inferred_with_vlan_inferred_reason()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1", pvid: 42).Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var mapping = Engine.Correlate(input).Mappings.Should().ContainSingle().Subject;
        mapping.Port.Vlans.Should().Equal(42);
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.VlanInferred);
        mapping.Port.ReasonCodes.Should().NotContain(ReasonCode.VlanContextMissing);
    }

    [Fact]
    public void Vlan_absent_leaves_empty_vlans_with_vlan_context_missing_reason()
    {
        var input = new SnapshotBuilder()
            .Switch("sw1", s => s.Port("ether1").Bridge("ether1", "00:11:22:33:44:55"))
            .Server("srv-a", sv => sv.Nic("eth0", "00:11:22:33:44:55"))
            .Build();

        var mapping = Engine.Correlate(input).Mappings.Should().ContainSingle().Subject;
        mapping.Port.Vlans.Should().BeEmpty();
        mapping.Port.ReasonCodes.Should().Contain(ReasonCode.VlanContextMissing);
        mapping.Port.ReasonCodes.Should().NotContain(ReasonCode.VlanInferred);
    }
}
