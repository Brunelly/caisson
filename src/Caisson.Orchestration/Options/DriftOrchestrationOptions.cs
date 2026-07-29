namespace Caisson.Orchestration.Options;

/// <summary>
/// Tunables for drift recompute orchestration (story #64, AC4/NFR5): the scheduler cadence and the
/// hybrid retention policy. Bound from the SAME <c>Drift:Computation</c> configuration section as
/// <c>Caisson.Drift.DriftComputationOptions</c> — that type owns <c>MaxItemsPerReport</c> (bound directly
/// by <c>Caisson.Infrastructure.DependencyInjection.DriftServiceCollectionExtensions</c>, since
/// Orchestration cannot be referenced back from the pure engine's options); this type owns everything
/// specific to scheduling/retention. All values have safe defaults so the feature works out of the box.
/// </summary>
public sealed class DriftOrchestrationOptions
{
    /// <summary>Configuration section this binds from (shared with <c>Caisson.Drift.DriftComputationOptions</c>).</summary>
    public const string SectionName = "Drift:Computation";

    /// <summary>Whether the periodic <c>DriftScheduler</c> sweep is enabled (disabled by default in tests for determinism).</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>How often the scheduler evaluates racks for a due recompute, in seconds.</summary>
    public int SchedulerPollSeconds { get; set; } = 30;

    /// <summary>Whether the periodic <c>DriftRetentionPruner</c> sweep is enabled (disabled by default in tests for determinism).</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention pruner runs, in seconds.</summary>
    public int RetentionPollSeconds { get; set; } = 3600;

    /// <summary>
    /// Hybrid retention (story #64, Q3's answered question): keep at most this many of a rack's newest
    /// drift reports, regardless of age.
    /// </summary>
    public int RetentionMaxReportsPerRack { get; set; } = 200;

    /// <summary>
    /// Hybrid retention: additionally, never keep a report older than this many days, even if it is
    /// within the <see cref="RetentionMaxReportsPerRack"/> count — a report survives only if it satisfies
    /// BOTH bounds.
    /// </summary>
    public int RetentionMaxDays { get; set; } = 180;
}
