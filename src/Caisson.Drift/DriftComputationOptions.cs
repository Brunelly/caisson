namespace Caisson.Drift;

/// <summary>
/// Bounds the pure engine enforces against device-controlled volume (story #64, NFR3). The operationally
/// configurable value lives on <c>Caisson.Orchestration.Options.DriftOrchestrationOptions</c>; callers
/// map it onto this small, EF-free options type before calling <see cref="DriftEngine.Compute"/>.
/// </summary>
public sealed class DriftComputationOptions
{
    /// <summary>Default cap on the number of <see cref="DriftItemResult"/> rows a single report may carry.</summary>
    public const int DefaultMaxItemsPerReport = 5_000;

    /// <summary>
    /// Maximum number of drift items a single computation may return. Applied AFTER the canonical
    /// (SubjectType, SubjectKey, DriftType) sort so truncation is itself deterministic — the same
    /// oversized input always yields the same truncated prefix.
    /// </summary>
    public int MaxItemsPerReport { get; init; } = DefaultMaxItemsPerReport;
}
