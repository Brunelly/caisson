using Caisson.Api.Contracts;
using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #29: role-based redaction of management-plane addresses and NIC MACs. Mirrors the intent of
/// <c>LiveUpdatesContractGuardTests</c> (never leak a sensitive field to an under-privileged caller) but
/// as behavioural tests rather than a property-name sweep, since these fields are legitimately present
/// for Operator/Admin — the guard here is "gated by role", not "never present".
/// </summary>
public sealed class ContractMappersRedactionTests
{
    [Fact]
    public void RedactManagementFields_nulls_the_address_fields_for_a_non_privileged_caller()
    {
        var fields = new Dictionary<string, string?>
        {
            ["serial"] = "SW-1",
            ["managementIp"] = "10.0.0.1",
            ["bmcAddress"] = "10.0.1.1",
            ["mgmtAddress"] = "10.0.2.1",
            ["hostname"] = "server-1",
        };

        var redacted = ContractMappers.RedactManagementFields(fields, isPrivileged: false)!;

        redacted["managementIp"].Should().BeNull();
        redacted["bmcAddress"].Should().BeNull();
        redacted["mgmtAddress"].Should().BeNull();
        // Non-address fields are untouched.
        redacted["serial"].Should().Be("SW-1");
        redacted["hostname"].Should().Be("server-1");
    }

    [Fact]
    public void RedactManagementFields_returns_full_values_for_a_privileged_caller()
    {
        var fields = new Dictionary<string, string?>
        {
            ["managementIp"] = "10.0.0.1",
            ["bmcAddress"] = "10.0.1.1",
        };

        var redacted = ContractMappers.RedactManagementFields(fields, isPrivileged: true)!;

        redacted["managementIp"].Should().Be("10.0.0.1");
        redacted["bmcAddress"].Should().Be("10.0.1.1");
    }

    [Fact]
    public void RedactManagementFields_passes_through_null_unchanged()
        => ContractMappers.RedactManagementFields(null, isPrivileged: false).Should().BeNull();

    [Fact]
    public void ToGraph_masks_the_nic_specific_portion_of_a_mac_for_a_non_privileged_caller()
    {
        var view = new TopologyGraphView(
            Guid.NewGuid(), 1, Guid.NewGuid(),
            new List<ServerNode>
            {
                new(
                    "dev-1|srv-1", "server-1", "uuid-1",
                    new List<NicNode> { new("aabbccddeeff", "eth0", "aa:bb:cc:dd:ee:ff", null, new List<PortAttachment>(), null) }),
            },
            new List<UnmappedPortNode>(),
            new List<SwitchInventoryNode>());

        var redacted = ContractMappers.ToGraph(view, isPrivileged: false);
        var privileged = ContractMappers.ToGraph(view, isPrivileged: true);

        redacted.Servers[0].Nics[0].Mac.Should().Be("aa:bb:cc:xx:xx:xx");
        privileged.Servers[0].Nics[0].Mac.Should().Be("aa:bb:cc:dd:ee:ff");
    }
}
