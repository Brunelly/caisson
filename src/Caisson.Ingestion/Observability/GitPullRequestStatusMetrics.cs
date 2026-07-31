using System.Diagnostics.Metrics;

namespace Caisson.Ingestion.Observability;

/// <summary>
/// Observability for the GitHub PR status poller (story #173, Task #218). A single <see cref="Meter"/> owning
/// the counters/histogram operators watch: poll attempts/results/duration, claimed rows, meaningful
/// transitions, poll-failures-by-reason, the count of GitHub calls made, and the last successful GitHub
/// contact (an observable gauge). Mirrors <see cref="GitIngestionMetrics"/>'s shape; registered as a singleton.
/// </summary>
public sealed class GitPullRequestStatusMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Ingestion.GitPrStatus";

    private readonly Meter _meter;
    private readonly Counter<long> _pollsAttempted;
    private readonly Counter<long> _pollsSucceeded;
    private readonly Counter<long> _pollsFailed;
    private readonly Histogram<double> _pollDurationSeconds;
    private readonly Counter<long> _rowsClaimed;
    private readonly Counter<long> _transitions;
    private readonly Counter<long> _gitHubCalls;

    private long _lastSuccessfulContactUnixSeconds;

    public GitPullRequestStatusMetrics()
    {
        _meter = new Meter(MeterName);
        _pollsAttempted = _meter.CreateCounter<long>(
            "caisson.git_pr_status.polls_attempted", unit: "{poll}", description: "Per-PR status poll attempts.");
        _pollsSucceeded = _meter.CreateCounter<long>(
            "caisson.git_pr_status.polls_succeeded", unit: "{poll}", description: "Per-PR status polls that completed both GitHub reads.");
        _pollsFailed = _meter.CreateCounter<long>(
            "caisson.git_pr_status.polls_failed", unit: "{poll}", description: "Per-PR status polls that failed, tagged by sanitized reason.");
        _pollDurationSeconds = _meter.CreateHistogram<double>(
            "caisson.git_pr_status.poll_duration_seconds", unit: "s", description: "Per-PR poll wall-clock duration.");
        _rowsClaimed = _meter.CreateCounter<long>(
            "caisson.git_pr_status.rows_claimed", unit: "{row}", description: "Status records claimed by the DB lease per tick.");
        _transitions = _meter.CreateCounter<long>(
            "caisson.git_pr_status.transitions", unit: "{transition}", description: "Meaningful (state/checks) PR status transitions.");
        _gitHubCalls = _meter.CreateCounter<long>(
            "caisson.git_pr_status.github_calls", unit: "{call}", description: "GitHub read API calls made by the poller (≤2 per PR per cycle, NFR1).");
        _meter.CreateObservableGauge(
            "caisson.git_pr_status.last_successful_contact_unixtime", () => _lastSuccessfulContactUnixSeconds,
            unit: "s", description: "Unix time of the last successful GitHub contact (0 = never).");
    }

    /// <summary>Records the number of rows claimed this tick.</summary>
    public void RecordRowsClaimed(int count) => _rowsClaimed.Add(count);

    /// <summary>Records that a per-PR poll was attempted.</summary>
    public void RecordPollAttempt() => _pollsAttempted.Add(1);

    /// <summary>Records a successful per-PR poll and its duration.</summary>
    public void RecordPollSuccess(TimeSpan duration)
    {
        _pollsSucceeded.Add(1);
        _pollDurationSeconds.Record(duration.TotalSeconds);
    }

    /// <summary>Records a failed per-PR poll, tagged by its sanitized reason code, and its duration.</summary>
    public void RecordPollFailure(string reasonCode, TimeSpan duration)
    {
        _pollsFailed.Add(1, new KeyValuePair<string, object?>("reason", reasonCode));
        _pollDurationSeconds.Record(duration.TotalSeconds);
    }

    /// <summary>Records a meaningful status transition.</summary>
    public void RecordTransition() => _transitions.Add(1);

    /// <summary>Records one GitHub read API call.</summary>
    public void RecordGitHubCall() => _gitHubCalls.Add(1);

    /// <summary>Marks a successful GitHub contact at <paramref name="atUtc"/> (drives the health check + gauge).</summary>
    public void RecordSuccessfulContact(DateTime atUtc)
        => Interlocked.Exchange(ref _lastSuccessfulContactUnixSeconds, new DateTimeOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
