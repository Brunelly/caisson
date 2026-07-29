namespace Caisson.Orchestration.Options;

/// <summary>
/// Tunables for the drift-apply job runner (story #65, AC4/AC5/NFR2). Bound from the
/// <c>DriftApply:Orchestration</c> configuration section; all values have safe defaults so the feature
/// works out of the box. Mirrors <see cref="DiscoveryOrchestrationOptions"/>'s shape.
/// </summary>
public sealed class DriftApplyOrchestrationOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "DriftApply:Orchestration";

    /// <summary>How often the job runner polls for claimable work when idle, in seconds.</summary>
    public int RunnerPollSeconds { get; set; } = 5;

    /// <summary>
    /// Heartbeat staleness threshold, in seconds: a non-terminal job whose heartbeat is older than this is
    /// considered abandoned (crashed host) and is reclaimed by the runner (NFR2: resume without duplicating
    /// the device write).
    /// </summary>
    public int HeartbeatStalenessSeconds { get; set; } = 45;

    /// <summary>Maximum attempts per step before it is marked failed (bounded retry).</summary>
    public int MaxStepAttempts { get; set; } = 3;

    /// <summary>Base delay for the exponential backoff between step retries, in milliseconds.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>Upper bound on a single backoff delay, in milliseconds.</summary>
    public int RetryMaxDelayMs { get; set; } = 5000;

    /// <summary>Whether the background runner is enabled (disabled in some tests for determinism).</summary>
    public bool RunnerEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of times a job may be claimed/attempted before it is excluded from the reclaim
    /// predicate and failed with a stable, non-retryable error code — the backstop against a job that
    /// reclaims, crashes, reclaims, crashes... forever.
    /// </summary>
    public int MaxJobAttempts { get; set; } = 5;
}
