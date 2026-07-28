using Caisson.Domain.ValueObjects;

namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// The single ground-truth definition for the story-11 virtual rack: one MikroTik-vendored switch and
/// one HPE-vendored server, with fixed MACs/UUIDs/serials (no <c>Guid.NewGuid()</c>/<c>Random</c> —
/// NFR2) whose wiring deliberately exercises every correlation band. <see cref="RouterOsProfileRenderer"/>
/// and <see cref="RedfishProfileRenderer"/> render this same definition into the two simulator profile
/// formats; <see cref="ExpectedTopologyBuilder"/> renders it into the correlation result the real engine
/// must reproduce. One definition, two renderers (plus the expectation) — nothing here is derived from
/// the simulators or the correlation engine, so a regression in either shows up as a diff, not a
/// tautology.
///
/// The wiring:
/// <list type="bullet">
/// <item><description><see cref="CleanNicName"/> (<see cref="CleanNicMac"/>) is learned on exactly one
/// port (<see cref="CleanPort"/>) which also carries an LLDP neighbour row from the same host — the
/// High-confidence, <c>MacLearnUnique</c> + <c>LldpConsistent</c> case.</description></item>
/// <item><description><see cref="AmbiguousNicName"/> (<see cref="AmbiguousNicMac"/>) is learned on two
/// distinct ports (<see cref="AmbiguousPortA"/>, <see cref="AmbiguousPortB"/>) with different VLANs — an
/// ambiguous mapping.</description></item>
/// <item><description><see cref="UnmappedNicName"/> (<see cref="UnmappedNicMac"/>) is reported by the
/// BMC but never appears in any switch bridge/LLDP table — an unmapped NIC
/// (<c>NotSeenInSwitch</c>).</description></item>
/// <item><description><see cref="UnmappedPort"/> learns a MAC (<see cref="ForeignMac"/>) that belongs to
/// no known server NIC — an unmapped port.</description></item>
/// </list>
/// </summary>
public static class VirtualRackDefinition
{
    /// <summary>The switch's device key (the natural id a rack definition assigns it).</summary>
    public const string SwitchId = "sw1";

    /// <summary>The server's device key.</summary>
    public const string ServerId = "srv1";

    // --- Switch identity. Deliberately no RouterBOARD serial (CHR has none), so the switch's stable key
    // falls back to management IP — always loopback for the in-process simulator, hence deterministic. ---
    public const string SwitchOsVersion = "7.15";
    public const string SwitchBoardName = "CHR";
    public const string SwitchPlatform = "MikroTik";

    // --- Server identity (fixed, no Guid.NewGuid()). ---
    public const string ServerUuid = "5c4b7a10-0000-4000-8000-000000000001";
    public const string ServerSerial = "VR-SN-0001";
    public const string ServerModel = "Caisson Virtual Server";
    public const string ServerManufacturer = "HPE";
    public const string ServerHostName = "vrack-srv1";
    public const string ServerBiosVersion = "VR-BIOS-1.0";

    // --- NIC MACs. Canonical colon form; each renderer is free to use its own textual format (the
    // Redfish renderer deliberately varies format per NIC to prove separator-agnostic MAC parsing). ---
    public const string CleanNicMac = "00:1A:2B:AA:AA:01";
    public const string AmbiguousNicMac = "00:1A:2B:AA:AA:02";
    public const string UnmappedNicMac = "00:1A:2B:AA:AA:03";

    /// <summary>A MAC seen on the switch that belongs to no known server NIC — the unmapped-port case.</summary>
    public const string ForeignMac = "00:1A:2B:AA:AA:FE";

    // --- NIC names (as reported by the BMC's EthernetInterfaces). ---
    public const string CleanNicName = "eth0";
    public const string AmbiguousNicName = "eth1";
    public const string UnmappedNicName = "eth2";

    // --- Switch ports. ---
    public const string CleanPort = "ether1";
    public const string AmbiguousPortA = "ether2";
    public const string AmbiguousPortB = "ether3";
    public const string UnmappedPort = "ether4";

    // --- VLANs: one access VLAN per port. None are trunk (a single untagged/PVID VLAN, no tagging). ---
    public const int CleanVlan = 10;
    public const int AmbiguousVlanA = 20;
    public const int AmbiguousVlanB = 30;
    public const int UnmappedPortVlan = 40;

    // --- LLDP: the clean NIC's host advertises LLDP on its own switch port. The chassis id is the NIC's
    // own MAC (a typical host LLDP TLV) — deliberately NOT a token that identifies another switch (not
    // SwitchId/ManagementIp/Serial), so the port stays classified access (not trunk) and the mapping
    // isn't demoted to the flat trunk-only confidence. ---
    public const string LldpChassisId = CleanNicMac;
    public const string LldpSystemName = ServerHostName;
    public const string LldpPortId = CleanNicName;
    public const string LldpMgmtAddress = "192.0.2.50";

    public static MacAddressValue CleanMac => MacAddressValue.Parse(CleanNicMac);

    public static MacAddressValue AmbiguousMac => MacAddressValue.Parse(AmbiguousNicMac);

    public static MacAddressValue UnmappedMac => MacAddressValue.Parse(UnmappedNicMac);

    public static MacAddressValue ForeignMacValue => MacAddressValue.Parse(ForeignMac);
}
