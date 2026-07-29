using Caisson.Domain.Enums;

namespace Caisson.Drift;

/// <summary>
/// The static, exhaustively-tested severity rule table for M1 drift reporting (story #64, Q2's answered
/// question: "static mapping per driftType ... keep extensible for later policy-based severity"). Every
/// <see cref="DriftType"/> member MUST have an entry — <see cref="DriftEngineTests"/>-adjacent
/// <c>DriftSeverityRulesTests</c> asserts this exhaustively so a future new <see cref="DriftType"/> value
/// fails the build loudly instead of silently falling through to a default.
/// </summary>
public static class DriftSeverityRules
{
    private static readonly IReadOnlyDictionary<DriftType, DriftSeverity> Table = new Dictionary<DriftType, DriftSeverity>
    {
        // A desired port that no longer exists at all: the most severe class of drift.
        [DriftType.MissingDesiredEntity] = DriftSeverity.High,

        // An extra observed port with no desired counterpart: informational, low urgency.
        [DriftType.ExtraObservedEntity] = DriftSeverity.Low,

        // A wrong access VLAN directly affects which broadcast domain a port's traffic lands in.
        [DriftType.AccessVlanMismatch] = DriftSeverity.High,

        // M1 desired intent declares no trunk configuration at all, so any tagged VLAN is unexpected but
        // not necessarily service-affecting on its own — worth attention, not urgent.
        [DriftType.UnexpectedTrunkConfig] = DriftSeverity.Medium,

        // A neighbour mismatch may indicate a cabling change; worth attention, not urgent.
        [DriftType.UnexpectedNeighbour] = DriftSeverity.Medium,

        // Ambiguous/unmapped NICs are a known-uncertain state (AC2), not a confirmed high-severity
        // finding, but still worth an operator's attention to resolve the ambiguity.
        [DriftType.UnknownTopologyMapping] = DriftSeverity.Medium,
    };

    /// <summary>Resolves the deterministic severity for a drift type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when no rule is defined (should never happen for a real <see cref="DriftType"/>).</exception>
    public static DriftSeverity For(DriftType driftType)
        => Table.TryGetValue(driftType, out var severity)
            ? severity
            : throw new ArgumentOutOfRangeException(nameof(driftType), driftType, "No severity rule is defined for this drift type.");
}
