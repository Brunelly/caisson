namespace Caisson.Domain.Drift;

/// <summary>
/// Single audited place for every bound the drift persistence model enforces (story #64), mirroring
/// <c>DesiredStateSchema</c>/<c>TopologyEntityDiff</c>'s centralised-bounds precedent so the entity
/// constructor guards and the EF Core <c>HasMaxLength</c> mappings in <c>Caisson.Infrastructure</c> can
/// never drift from one another.
/// </summary>
public static class DriftSchema
{
    /// <summary>The drift engine's rule-set version stamped on every computed <see cref="DriftReport"/>.</summary>
    public const int CurrentComputationVersion = 1;

    /// <summary>Maximum length of a <see cref="DriftItem.SubjectKey"/> (mirrors <c>TopologyEntityDiff.EntityStableKey</c>).</summary>
    public const int MaxSubjectKeyLength = 512;

    /// <summary>Maximum length of a <see cref="DriftItem.ExpectedValue"/>.</summary>
    public const int MaxExpectedValueLength = 1024;

    /// <summary>Maximum length of a <see cref="DriftItem.ActualValue"/>.</summary>
    public const int MaxActualValueLength = 1024;

    /// <summary>Maximum length of a <see cref="DriftItem.Why"/> explanation.</summary>
    public const int MaxWhyLength = 2048;

    /// <summary>Maximum length of the bounded <see cref="DriftItem.DetailsJson"/> payload.</summary>
    public const int MaxDetailsJsonLength = 8192;

    /// <summary>Maximum length of the bounded <see cref="DriftReport.CountsBySeverityJson"/> payload.</summary>
    public const int MaxCountsBySeverityJsonLength = 2048;

    /// <summary>Maximum length of a <see cref="DriftReport.ErrorSummary"/>.</summary>
    public const int MaxErrorSummaryLength = 2048;
}
