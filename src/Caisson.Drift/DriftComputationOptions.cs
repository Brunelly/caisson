namespace Caisson.Drift;

/// <summary>
/// Bounds the pure engine enforces against device-controlled volume (story #64, NFR3). Bound directly
/// from the shared <c>Drift:Computation</c> configuration section by
/// <c>Caisson.Infrastructure.DependencyInjection.DriftServiceCollectionExtensions</c> — the scheduler/
/// event-driven/retention settings for the SAME section live on the separate
/// <c>Caisson.Orchestration.Options.DriftOrchestrationOptions</c> (Orchestration cannot reference this
/// Domain-only pure engine's options type without violating the layering the engine's purity guard
/// enforces the other direction, so the one section is bound onto two small, independently-owned POCOs).
/// </summary>
public sealed class DriftComputationOptions
{
    /// <summary>The shared configuration section both this and <c>DriftOrchestrationOptions</c> bind from.</summary>
    public const string SectionName = "Drift:Computation";

    /// <summary>Default cap on the number of <see cref="DriftItemResult"/> rows a single report may carry.</summary>
    public const int DefaultMaxItemsPerReport = 5_000;

    /// <summary>
    /// Maximum number of drift items a single computation may return. Applied AFTER the canonical
    /// (SubjectType, SubjectKey, DriftType) sort so truncation is itself deterministic — the same
    /// oversized input always yields the same truncated prefix.
    /// </summary>
    public int MaxItemsPerReport { get; init; } = DefaultMaxItemsPerReport;
}
