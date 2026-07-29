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

    /// <summary>
    /// Overall wall-clock budget for a single job, in seconds, from its first start (finding #12). Exceeding
    /// it fails the job with <see cref="Discovery.DiscoveryErrorCodes.JobTimedOut"/> rather than letting a
    /// stuck job run (and re-heartbeat) indefinitely.
    /// </summary>
    public int MaxJobDurationSeconds { get; set; } = 1800;

    /// <summary>
    /// Wall-clock budget for a single step attempt, in seconds (finding #12). Enforced as a
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> deadline linked into the token passed to
    /// the step's device calls, so a hung driver call cannot silently hold a step open forever.
    /// </summary>
    public int MaxStepDurationSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of times a job may be claimed/attempted (finding #12) before it is excluded from
    /// <c>ClaimNextAsync</c>'s reclaim predicate and failed with a stable, non-retryable error code — the
    /// backstop against a job that reclaims, crashes, reclaims, crashes... forever.
    /// </summary>
    public int MaxJobAttempts { get; set; } = 5;

    /// <summary>
    /// Maximum ports accepted from one switch per discovery run (finding #11). Medium-rack default: a
    /// 48-port switch comfortably fits; this bounds what a compromised/misbehaving device can inflate the
    /// in-memory correlation engine and the persisted graph with.
    /// </summary>
    public int MaxPortsPerSwitch { get; set; } = 512;

    /// <summary>Maximum bridge/MAC-learning host-table entries accepted from one switch per run (finding #11).</summary>
    public int MaxBridgeHostsPerSwitch { get; set; } = 16_384;

    /// <summary>Maximum LLDP neighbours accepted from one switch per run (finding #11).</summary>
    public int MaxLldpNeighboursPerSwitch { get; set; } = 2_048;

    /// <summary>Maximum NICs accepted from one server per run (finding #11).</summary>
    public int MaxNicsPerServer { get; set; } = 64;

    /// <summary>
    /// Maximum ranked port candidates retained per ambiguous NIC (finding #11) — bounds
    /// <c>TopologyCorrelationEngine.ResolveAmbiguous</c>'s output (and, downstream, the
    /// <c>topology_candidate_mapping</c> rows persisted for one NIC) to the top-K by score.
    /// </summary>
    public int MaxCandidatesPerNic { get; set; } = 16;
}
