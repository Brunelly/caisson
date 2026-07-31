namespace Caisson.Correlation;

/// <summary>
/// The fixed, documented rule-based scoring weights and heuristic thresholds used by
/// <see cref="TopologyCorrelationEngine"/>. Centralised so the scoring model is auditable in one place
/// and mirrored by docs/topology-correlation.md and the unit tests (AC6). Scores are additive and always
/// clamped to <c>[0,1]</c> before a <see cref="Caisson.Domain.ValueObjects.ConfidenceScore"/> is built.
/// </summary>
internal static class CorrelationScoring
{
    /// <summary>
    /// Base confidence for a MAC learned on a single access/edge port — the strongest attachment signal.
    /// </summary>
    public const double BaseBridgeHit = 0.70;

    /// <summary>Bonus when the port has an LLDP neighbour that does not contradict the mapping.</summary>
    public const double LldpConsistentBonus = 0.25;

    /// <summary>
    /// Smaller bonus when the port has no LLDP at all: the bridge table alone still maps, but with less
    /// corroboration (the missing-LLDP fallback).
    /// </summary>
    public const double MissingLldpBonus = 0.15;

    /// <summary>
    /// Confidence for a MAC seen only on a trunk/uplink port — a transiting MAC, not a reliable direct
    /// attachment. Deliberately in the Low band so the score itself communicates the caveat.
    /// </summary>
    public const double TrunkOnlyConfidence = 0.15;

    /// <summary>Equal, boosted score given to ports detected as members of the same LAG (Medium band).</summary>
    public const double LagBoostedScore = 0.65;

    /// <summary>
    /// Multiplicative penalty applied to each candidate of a non-LAG ambiguous MAC, pulling competing
    /// candidates below a confident single mapping while preserving their relative order.
    /// </summary>
    public const double AmbiguityPenaltyFactor = 0.60;

    /// <summary>
    /// Distinct-MAC count above which a port is treated as a trunk/uplink (an access port normally learns
    /// only the handful of MACs behind a single attached host). Sourced from the shared
    /// <see cref="PortRoleClassifier.TrunkMacCountThreshold"/> so the scoring model and the story-#170
    /// rack-inventory projector share one threshold.
    /// </summary>
    public const int TrunkMacCountThreshold = PortRoleClassifier.TrunkMacCountThreshold;

    /// <summary>
    /// Maximum distinct MACs on an access port for the <c>MacLearnUnique</c> signal (a clean 1:1 learn).
    /// </summary>
    public const int AccessUniqueMaxHosts = 1;

    /// <summary>Decimal places scores are rounded to before ordering, for stable deterministic tie-breaks.</summary>
    public const int ScorePrecision = 6;
}
