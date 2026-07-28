namespace Caisson.Orchestration.Options;

/// <summary>
/// Tunables for the discovery job runner and scheduler (story #8). Bound from the
/// <c>Discovery:Orchestration</c> configuration section; all values have safe defaults so the feature
/// works out of the box.
/// </summary>
public sealed class DiscoveryOrchestrationOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Discovery:Orchestration";

    /// <summary>How often the job runner polls for claimable work when idle, in seconds.</summary>
    public int RunnerPollSeconds { get; set; } = 5;

    /// <summary>How often the scheduler evaluates due schedules, in seconds.</summary>
    public int SchedulerPollSeconds { get; set; } = 30;

    /// <summary>
    /// Heartbeat staleness threshold, in seconds: an <c>InProgress</c> job whose heartbeat is older than
    /// this is considered abandoned (crashed host) and is reclaimed by the runner (NFR1: resume ≤ 60s).
    /// </summary>
    public int HeartbeatStalenessSeconds { get; set; } = 45;

    /// <summary>Maximum attempts per driver step before it is marked failed (NFR1, bounded retry).</summary>
    public int MaxStepAttempts { get; set; } = 3;

    /// <summary>Base delay for the exponential backoff between step retries, in milliseconds.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>Upper bound on a single backoff delay, in milliseconds.</summary>
    public int RetryMaxDelayMs { get; set; } = 5000;

    /// <summary>Per-device driver call timeout, in seconds (used when a device omits its own timeout).</summary>
    public int DefaultDeviceTimeoutSeconds { get; set; } = 30;

    /// <summary>Whether the background runner is enabled (disabled in some tests for determinism).</summary>
    public bool RunnerEnabled { get; set; } = true;

    /// <summary>Whether the background scheduler is enabled (disabled in some tests for determinism).</summary>
    public bool SchedulerEnabled { get; set; } = true;
}
