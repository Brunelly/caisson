namespace Caisson.Domain.Enums;

/// <summary>
/// Why a candidate NIC-to-switch-port mapping is unmapped, ambiguous, or otherwise noteworthy.
/// Stored per candidate alongside a bounded confidence score (see <see cref="ReasonCode"/> usage on
/// <c>TopologyCandidateMapping</c>).
/// </summary>
public enum ReasonCode
{
    /// <summary>No specific reason recorded.</summary>
    Unknown = 0,

    /// <summary>The MAC was observed from BMC inventory but not in any switch bridge/LLDP table.</summary>
    NotSeenInSwitch,

    /// <summary>The MAC was observed on a switch but not in any BMC inventory.</summary>
    NotSeenInBmc,

    /// <summary>No LLDP evidence was available to correlate the endpoint.</summary>
    MissingLldp,

    /// <summary>Multiple sources disagree about where the MAC lives.</summary>
    ConflictingMacEvidence,

    /// <summary>The same MAC was claimed by more than one switch port in the snapshot.</summary>
    DuplicateMac,

    /// <summary>The evidence used for correlation was stale.</summary>
    StaleData,

    /// <summary>The device could not be reached during discovery.</summary>
    DeviceUnreachable,

    /// <summary>Authentication to the device failed during discovery.</summary>
    AuthenticationFailed,

    /// <summary>The source data could not be parsed.</summary>
    ParseError,

    /// <summary>
    /// The data for this item was obtained from a fallback source rather than the primary one (e.g. read
    /// via IPMI because Redfish was unavailable or returned insufficient data). Records provenance so
    /// downstream correlation can weight or audit the evidence.
    /// </summary>
    FallbackSource,

    /// <summary>The MAC was learned on exactly one access/edge port, the strongest attachment signal.</summary>
    MacLearnUnique,

    /// <summary>An LLDP neighbour on the port is consistent with (does not contradict) the mapping.</summary>
    LldpConsistent,

    /// <summary>An LLDP neighbour on the port identifies a different device, contradicting the mapping.</summary>
    LldpContradicts,

    /// <summary>The same MAC was learned on more than one candidate port, producing ambiguity.</summary>
    MultipleMacPorts,

    /// <summary>The candidate ports share a switch and identical VLAN config, so they look like one LAG.</summary>
    PortsInSameLag,

    /// <summary>The MAC was only seen on a trunk/uplink port, which is not a reliable direct attachment.</summary>
    SeenOnTrunkPort,

    /// <summary>One or more VLANs were inferred for the port from its Pvid/tagged-VLAN context.</summary>
    VlanInferred,

    /// <summary>No VLAN/bridge context was available for the port, so VLAN membership is unknown.</summary>
    VlanContextMissing,

    /// <summary>The port has an LLDP neighbour but it could not be correlated to any known NIC.</summary>
    PortNeighbourUnknown,

    /// <summary>
    /// A second entity computed the same stable key as one already seen in the snapshot and was skipped
    /// rather than silently overwriting the first (finding #3) — e.g. two switches reporting an identical
    /// serial, or two servers reporting an identical UUID/MAC.
    /// </summary>
    StableKeyCollision,

    /// <summary>A device-supplied field exceeded its column limit and was truncated before persistence (finding #28).</summary>
    FieldTruncated,

    /// <summary>A device reported the same MAC address on more than one NIC within the same snapshot (finding #3).</summary>
    DuplicateNicMac,

    /// <summary>
    /// A device reported more entries (ports/bridge-hosts/LLDP neighbours/NICs) than the configured cap
    /// and was truncated (finding #11) — bounds device-controlled volume flowing into the in-memory
    /// correlation engine and the persisted snapshot.
    /// </summary>
    VolumeCapped,
}
