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
}
