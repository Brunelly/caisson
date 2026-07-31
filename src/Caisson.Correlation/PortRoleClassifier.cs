namespace Caisson.Correlation;

/// <summary>
/// The single, pure, public port trunk/uplink classification rule (story #10 correlation heuristics,
/// reused by story #170 pre-flight safety warnings). Extracted from the internal <see cref="SnapshotIndex"/>
/// so both the correlation engine and the Infrastructure rack-inventory projector classify a port with the
/// identical, already-unit-tested rule, threshold and token normalization rather than each re-deriving a
/// bespoke trunk heuristic (<c>SnapshotIndex</c> operates on the driver-input records, the projector on the
/// persisted EF graph — same rule, different carriers). Deterministic and side-effect free.
/// </summary>
public static class PortRoleClassifier
{
    /// <summary>
    /// The learned-MAC count above which a port is treated as a trunk/uplink even without an LLDP
    /// peer-switch signal or multi-VLAN tagging — a port carrying many hosts' MACs is transit, not edge.
    /// Mirrors <c>docs/topology-correlation.md</c> / ADR 0010.
    /// </summary>
    public const int TrunkMacCountThreshold = 4;

    /// <summary>
    /// Classifies a port as trunk/uplink from the combined documented signals: an LLDP neighbour that is
    /// another switch (the primary uplink signal), more than one tagged VLAN, or a learned-MAC count above
    /// <see cref="TrunkMacCountThreshold"/>. LLDP peer-switch is the primary signal; multi-VLAN tagging and
    /// the high learned-MAC count are the fallbacks.
    /// </summary>
    public static bool IsTrunk(bool peerSwitchLldp, int taggedVlanCount, int learnedMacCount)
        => peerSwitchLldp
            || taggedVlanCount > 1
            || learnedMacCount > TrunkMacCountThreshold;

    /// <summary>
    /// Normalizes a switch-identity or LLDP token for comparison (trim + lower-invariant), returning
    /// <c>null</c> for blank input. Shared so peer-switch LLDP matching folds tokens identically wherever it
    /// runs.
    /// </summary>
    public static string? NormalizeToken(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : token.Trim().ToLowerInvariant();
}
