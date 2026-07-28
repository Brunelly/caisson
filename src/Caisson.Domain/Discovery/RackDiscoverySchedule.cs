namespace Caisson.Domain.Discovery;

/// <summary>
/// Per-rack recurring discovery schedule (story #8, AC3). One row per rack (1:1 with <c>Rack</c>). M0
/// supports a simple fixed interval plus jitter only — deliberately no cron field (see ADR 0013). It is
/// a mutable registry-style entity: the scheduler advances <see cref="NextRunAtUtc"/> and stamps the
/// attempt/success timestamps in place on every tick.
/// </summary>
public sealed class RackDiscoverySchedule
{
    private RackDiscoverySchedule()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates a schedule for a rack.</summary>
    public RackDiscoverySchedule(
        Guid rackId,
        bool enabled,
        int intervalSeconds,
        int jitterSeconds,
        DateTime? nextRunAtUtc = null)
    {
        RackId = rackId;
        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
        JitterSeconds = jitterSeconds;
        NextRunAtUtc = nextRunAtUtc;
    }

    /// <summary>The rack this schedule governs (primary key, 1:1 with <c>Rack</c>).</summary>
    public Guid RackId { get; private set; }

    /// <summary>Whether the scheduler should create runs for this rack.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Fixed interval between runs, in seconds.</summary>
    public int IntervalSeconds { get; private set; }

    /// <summary>Maximum random jitter added to the interval, in seconds (0 disables jitter).</summary>
    public int JitterSeconds { get; private set; }

    /// <summary>When the next run is due; null defers to the first tick after enabling.</summary>
    public DateTime? NextRunAtUtc { get; private set; }

    /// <summary>When the scheduler last attempted a run for this rack (regardless of outcome).</summary>
    public DateTime? LastAttemptAtUtc { get; private set; }

    /// <summary>When a scheduled run for this rack last succeeded.</summary>
    public DateTime? LastSuccessAtUtc { get; private set; }

    /// <summary>Enables/updates the schedule cadence (Admin-managed, AC4).</summary>
    public void Configure(bool enabled, int intervalSeconds, int jitterSeconds, DateTime? nextRunAtUtc)
    {
        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
        JitterSeconds = jitterSeconds;
        NextRunAtUtc = nextRunAtUtc;
    }

    /// <summary>Records that a tick attempted a run and advances the next-run time (AC3).</summary>
    public void RecordAttempt(DateTime attemptedAtUtc, DateTime nextRunAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        NextRunAtUtc = nextRunAtUtc;
    }

    /// <summary>Records a successful scheduled run for the rack (AC3/AC4).</summary>
    public void RecordSuccess(DateTime succeededAtUtc) => LastSuccessAtUtc = succeededAtUtc;
}
