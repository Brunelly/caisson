namespace Caisson.Domain.Enums;

/// <summary>
/// The operational severity of a <c>DriftItem</c>, assigned deterministically per <c>DriftType</c> by a
/// static rule table (story #64, Q2). Persisted as a bounded string.
/// </summary>
public enum DriftSeverity
{
    /// <summary>Informational drift with limited operational impact.</summary>
    Low = 0,

    /// <summary>Drift worth operator attention but not urgent.</summary>
    Medium,

    /// <summary>Drift that likely affects service correctness or connectivity.</summary>
    High,
}
