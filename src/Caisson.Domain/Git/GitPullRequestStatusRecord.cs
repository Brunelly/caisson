namespace Caisson.Domain.Git;

/// <summary>
/// The durable, mutable projection of a GitHub pull request's <em>current</em> state and check-run rollup for
/// a published <see cref="GitPullRequestLink"/> (story #173, Task #210). Exactly one record exists per link
/// (1:1, enforced by a unique index on <see cref="PullRequestLinkId"/>); the poller upserts it each cycle.
/// <para>
/// A distinct type from the pre-existing <see cref="GitPullRequestStatus"/> enum (which it reuses for
/// <see cref="State"/>). Denormalizes <see cref="RackId"/>/<see cref="RepoOwner"/>/<see cref="RepoName"/>/
/// <see cref="PullRequestNumber"/>/<see cref="PullRequestUrl"/> from the link so rack-scoped reads and the
/// SignalR event need no join. Deliberately a MUTABLE POCO with private setters and NOT
/// <c>Caisson.Domain.Topology.IAppendOnly</c> (modelled on <see cref="GitPullRequestLink"/>): the poller
/// upserts it in place, which the append-only sweep would otherwise block.
/// </para>
/// <para>
/// All transition logic lives here as DB-free, unit-testable behaviour: <see cref="ApplyObservation"/> applies
/// a fresh GitHub observation and reports whether a <em>meaningful</em> (state or checks-conclusion) transition
/// occurred; <see cref="RecordPollSuccess"/>/<see cref="RecordPollFailure"/> drive the lease/backoff schedule.
/// </para>
/// </summary>
public sealed class GitPullRequestStatusRecord
{
    /// <summary>Maximum length of a GitHub repository owner/name segment (matches the link).</summary>
    public const int MaxRepoSegmentLength = GitPullRequestLink.MaxRepoSegmentLength;

    /// <summary>Maximum length of the stored pull-request URL (matches the link).</summary>
    public const int MaxUrlLength = GitPullRequestLink.MaxUrlLength;

    /// <summary>Maximum length of a commit/head SHA (matches the link).</summary>
    public const int MaxHeadShaLength = GitPullRequestLink.MaxCommitShaLength;

    /// <summary>Maximum length of the compact per-check JSONB rollup.</summary>
    public const int MaxChecksSummaryLength = 16_384;

    /// <summary>Maximum length of the sanitized poll-failure reason code.</summary>
    public const int MaxFailureReasonLength = 64;

    private GitPullRequestStatusRecord()
    {
        // EF Core materialization constructor.
        RepoOwner = null!;
        RepoName = null!;
        PullRequestUrl = null!;
    }

    /// <summary>
    /// First-sights the status record for a freshly-published link, before the first GitHub poll. The record
    /// is created "due now" (<see cref="NextPollAfterUtc"/> = <paramref name="createdAtUtc"/>) with an
    /// <see cref="GitPullRequestStatus.Open"/> state and an <see cref="GitPullRequestChecksConclusion.Unknown"/>
    /// conclusion; the first <see cref="ApplyObservation"/> fills in the real values.
    /// </summary>
    public GitPullRequestStatusRecord(
        Guid id,
        Guid pullRequestLinkId,
        Guid rackId,
        string repoOwner,
        string repoName,
        int pullRequestNumber,
        string pullRequestUrl,
        DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoOwner);
        ArgumentException.ThrowIfNullOrEmpty(repoName);
        ArgumentException.ThrowIfNullOrEmpty(pullRequestUrl);

        Id = id;
        PullRequestLinkId = pullRequestLinkId;
        RackId = rackId;
        RepoOwner = Bound(repoOwner, MaxRepoSegmentLength, nameof(repoOwner));
        RepoName = Bound(repoName, MaxRepoSegmentLength, nameof(repoName));
        PullRequestNumber = pullRequestNumber;
        PullRequestUrl = Bound(pullRequestUrl, MaxUrlLength, nameof(pullRequestUrl));
        State = GitPullRequestStatus.Open;
        ChecksConclusion = GitPullRequestChecksConclusion.Unknown;
        LastCheckedAtUtc = createdAtUtc;
        NextPollAfterUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>FK to the owning <see cref="GitPullRequestLink"/> (unique — 1:1).</summary>
    public Guid PullRequestLinkId { get; private set; }

    /// <summary>The rack this PR belongs to (denormalized for rack-scoped reads/events).</summary>
    public Guid RackId { get; private set; }

    /// <summary>The GitHub repository owner (denormalized).</summary>
    public string RepoOwner { get; private set; }

    /// <summary>The GitHub repository name (denormalized).</summary>
    public string RepoName { get; private set; }

    /// <summary>The pull request number (denormalized).</summary>
    public int PullRequestNumber { get; private set; }

    /// <summary>The pull request URL (denormalized).</summary>
    public string PullRequestUrl { get; private set; }

    /// <summary>The last-observed PR lifecycle state (open/closed/merged).</summary>
    public GitPullRequestStatus State { get; private set; }

    /// <summary>The last-observed PR head commit SHA, or <c>null</c> before the first successful poll.</summary>
    public string? HeadSha { get; private set; }

    /// <summary>The last-observed rolled-up check conclusion for <see cref="HeadSha"/>.</summary>
    public GitPullRequestChecksConclusion ChecksConclusion { get; private set; }

    /// <summary>The number of failing check runs at the last observation, or <c>null</c> if unknown.</summary>
    public int? FailingChecksCount { get; private set; }

    /// <summary>The compact per-check JSONB rollup (name/status/conclusion/detailsUrl/timings + truncation).</summary>
    public string? ChecksSummary { get; private set; }

    /// <summary>When the record was last polled (lease timestamp + the UI's "Last checked").</summary>
    public DateTime LastCheckedAtUtc { get; private set; }

    /// <summary>The earliest time the poller may claim this record again (backoff / rate-limit scheduling).</summary>
    public DateTime NextPollAfterUtc { get; private set; }

    /// <summary>Consecutive poll failures since the last success (drives exponential backoff).</summary>
    public int ConsecutivePollFailures { get; private set; }

    /// <summary>The sanitized reason code of the most recent poll failure, or <c>null</c> after a success.</summary>
    public string? LastPollFailureReason { get; private set; }

    /// <summary>When a real data change was last observed (state/checks/head/summary) — the "Last updated".</summary>
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Applies a successful GitHub observation. Updates all observed fields, clears any prior failure state,
    /// stamps <see cref="LastCheckedAtUtc"/>, and — when any real datum changed — <see cref="UpdatedAtUtc"/>.
    /// Returns <c>true</c> only when a <b>meaningful</b> transition occurred, i.e. <see cref="State"/> or
    /// <see cref="ChecksConclusion"/> changed; a head-SHA-only change moves <see cref="UpdatedAtUtc"/> but is
    /// NOT a meaningful transition (and so must NOT audit/emit an event).
    /// </summary>
    public bool ApplyObservation(
        GitPullRequestStatus state,
        string? headSha,
        GitPullRequestChecksConclusion checksConclusion,
        int? failingChecksCount,
        string? checksSummary,
        DateTime atUtc)
    {
        var boundedHeadSha = headSha is null ? null : Bound(headSha, MaxHeadShaLength, nameof(headSha));
        var boundedSummary = checksSummary is null
            ? null
            : Bound(checksSummary, MaxChecksSummaryLength, nameof(checksSummary));

        var meaningfulTransition = State != state || ChecksConclusion != checksConclusion;
        var realChange = meaningfulTransition
            || HeadSha != boundedHeadSha
            || FailingChecksCount != failingChecksCount
            || ChecksSummary != boundedSummary;

        State = state;
        HeadSha = boundedHeadSha;
        ChecksConclusion = checksConclusion;
        FailingChecksCount = failingChecksCount;
        ChecksSummary = boundedSummary;

        LastCheckedAtUtc = atUtc;
        if (realChange)
        {
            UpdatedAtUtc = atUtc;
        }

        // A successful observation clears any accumulated failure state.
        ConsecutivePollFailures = 0;
        LastPollFailureReason = null;

        return meaningfulTransition;
    }

    /// <summary>
    /// Records a successful poll's next-due schedule (typically <c>now + poll interval</c>). Kept separate from
    /// <see cref="ApplyObservation"/> so the observed data and the lease schedule can be reasoned/tested apart.
    /// </summary>
    public void RecordPollSuccess(DateTime nextPollAfterUtc) => NextPollAfterUtc = nextPollAfterUtc;

    /// <summary>
    /// Records a failed poll attempt: increments <see cref="ConsecutivePollFailures"/>, stores the sanitized
    /// <paramref name="reasonCode"/>, and schedules the (backoff/rate-limit-aware) <paramref name="nextPollAfterUtc"/>.
    /// The last-known <see cref="State"/>/<see cref="ChecksConclusion"/> are preserved (kept visible in the UI)
    /// and <see cref="UpdatedAtUtc"/> is NOT moved — a transient failure is not a status transition.
    /// </summary>
    public void RecordPollFailure(string reasonCode, DateTime nextPollAfterUtc, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);

        ConsecutivePollFailures += 1;
        LastPollFailureReason = Bound(reasonCode, MaxFailureReasonLength, nameof(reasonCode));
        NextPollAfterUtc = nextPollAfterUtc;
        LastCheckedAtUtc = atUtc;
    }

    private static string Bound(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value exceeds the {maxLength}-character bound.", paramName);
        }

        return value;
    }
}
