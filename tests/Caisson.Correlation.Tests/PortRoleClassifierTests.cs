using Caisson.Correlation;
using FluentAssertions;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>
/// Tests for the shared, public port trunk/uplink rule extracted from <c>SnapshotIndex</c> (story #170) so
/// both the correlation engine and the Infrastructure rack-inventory projector classify identically.
/// </summary>
public sealed class PortRoleClassifierTests
{
    [Fact]
    public void An_lldp_peer_switch_makes_a_port_a_trunk_regardless_of_other_signals()
        => PortRoleClassifier.IsTrunk(peerSwitchLldp: true, taggedVlanCount: 0, learnedMacCount: 0)
            .Should().BeTrue();

    [Fact]
    public void More_than_one_tagged_vlan_makes_a_port_a_trunk()
        => PortRoleClassifier.IsTrunk(peerSwitchLldp: false, taggedVlanCount: 2, learnedMacCount: 0)
            .Should().BeTrue();

    [Fact]
    public void A_learned_mac_count_above_the_threshold_makes_a_port_a_trunk()
    {
        PortRoleClassifier.IsTrunk(false, 0, PortRoleClassifier.TrunkMacCountThreshold + 1).Should().BeTrue();
        PortRoleClassifier.IsTrunk(false, 0, PortRoleClassifier.TrunkMacCountThreshold).Should().BeFalse();
    }

    [Fact]
    public void A_single_tagged_vlan_edge_port_is_not_a_trunk()
        => PortRoleClassifier.IsTrunk(peerSwitchLldp: false, taggedVlanCount: 1, learnedMacCount: 1)
            .Should().BeFalse();

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("  SW-1 ", "sw-1")]
    [InlineData("AA:BB", "aa:bb")]
    public void Normalize_token_trims_and_lowercases_returning_null_for_blank(string? input, string? expected)
        => PortRoleClassifier.NormalizeToken(input).Should().Be(expected);
}
