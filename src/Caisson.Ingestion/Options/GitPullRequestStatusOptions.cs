using System.ComponentModel.DataAnnotations;

namespace Caisson.Ingestion.Options;

/// <summary>
/// Control-plane configuration for the GitHub PR status poller (story #173, Task #211b), config-bound under
/// <see cref="SectionName"/>. Carries NO secret-shaped field — the GitHub credential resolves through
/// <c>IGitCredentialProvider</c> (Key Vault / managed identity), never through this POCO. Per-environment
/// defaults (dev/CI 60s, prod 300s) are set in <c>appsettings*.json</c>; the code default is the conservative
/// production cadence.
/// </summary>
public sealed class GitPullRequestStatusOptions
{
    /// <summary>Configuration section name (<c>GitPullRequestStatus</c>).</summary>
    public const string SectionName = "GitPullRequestStatus";

    /// <summary>The lower bound (seconds) on the poll interval — rate-limit-aware (NFR1).</summary>
    public const int MinPollIntervalSeconds = 60;

    /// <summary>The upper bound (seconds) on the poll interval (10 minutes).</summary>
    public const int MaxPollIntervalSeconds = 600;

    /// <summary>Whether the PR status poller is active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Poll interval, in seconds (bounded [60, 600]; NFR1). Prod default 300, dev/CI 60 via config.</summary>
    [Range(MinPollIntervalSeconds, MaxPollIntervalSeconds)]
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum number of due PRs claimed and polled per tick.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Upper bound (seconds) on the exponential backoff-with-jitter applied after a failed poll.</summary>
    [Range(1, 86_400)]
    public int MaxBackoffSeconds { get; set; } = 600;

    /// <summary>
    /// The lease horizon (seconds): on claim, <c>NextPollAfterUtc</c> jumps forward this far so another replica
    /// never re-claims the same PR mid-poll, and a crashed poll becomes due again after the lease expires.
    /// </summary>
    [Range(30, 3_600)]
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>
    /// The health-check threshold (minutes): the poller is reported Degraded when the newest successful poll is
    /// older than this or GitHub has been unreachable for longer (NFR3).
    /// </summary>
    [Range(1, 1_440)]
    public int DegradedAfterMinutes { get; set; } = 15;
}
