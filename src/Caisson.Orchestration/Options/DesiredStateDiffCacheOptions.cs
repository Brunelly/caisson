namespace Caisson.Orchestration.Options;

/// <summary>
/// Tunables for the impact-preview diff cache and its TTL pruner (story #171, Task #197). Bound from the
/// <c>DesiredState:DiffCache</c> configuration section. All values have safe defaults so the feature works
/// out of the box; the pruner is disabled by default in tests for determinism.
/// </summary>
public sealed class DesiredStateDiffCacheOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "DesiredState:DiffCache";

    /// <summary>Whether the periodic <c>DesiredStateDiffCachePruner</c> sweep is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the pruner runs, in seconds.</summary>
    public int PollSeconds { get; set; } = 900;

    /// <summary>
    /// How long a cached preview lives before it becomes eligible for pruning, in minutes. The row's
    /// <c>ExpiresAtUtc</c> is stamped <c>CreatedAtUtc + TtlMinutes</c> at insert.
    /// </summary>
    public int TtlMinutes { get; set; } = 1440;

    /// <summary>Maximum rows deleted per pruner pass, bounding each sweep's write amplification.</summary>
    public int PruneBatchSize { get; set; } = 500;
}
