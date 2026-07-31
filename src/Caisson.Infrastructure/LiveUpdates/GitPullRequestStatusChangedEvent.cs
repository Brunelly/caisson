namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Broadcast on each <em>meaningful</em> PR status transition (story #173, Task #212): a change in the PR
/// lifecycle state (Open/Merged/Closed) or the rolled-up checks conclusion. Emitted by the poller's transition
/// choke point AFTER the audit+status commit, and only when a real transition occurred (no-op polls emit
/// nothing). <see cref="Seq"/> is a cluster-monotonic per-link sequence
/// (<see cref="TopologyStreams.ForPullRequest"/>) so ordering holds across API instances; clients ignore an
/// older <c>(linkId, seq)</c>. By construction it carries only ids/status/counts/timestamps — never a token or
/// raw device data (NFR5, no-secrets contract guard).
/// </summary>
/// <param name="RackId">The rack the PR belongs to (SignalR group scoping).</param>
/// <param name="RepoOwner">The GitHub repository owner.</param>
/// <param name="RepoName">The GitHub repository name.</param>
/// <param name="PullRequestNumber">The pull request number.</param>
/// <param name="PullRequestUrl">The pull request URL.</param>
/// <param name="State">The new lifecycle state (Open|Merged|Closed).</param>
/// <param name="HeadSha">The observed head commit SHA, when known.</param>
/// <param name="ChecksConclusion">The new rolled-up checks conclusion.</param>
/// <param name="FailingChecksCount">The number of failing checks, when known.</param>
/// <param name="UpdatedAt">When the last real data change occurred (UTC).</param>
/// <param name="LastCheckedAt">When the PR was last polled (UTC).</param>
/// <param name="Seq">The cluster-monotonic per-link ordering sequence.</param>
/// <param name="CorrelationId">The tick correlation id.</param>
public sealed record GitPullRequestStatusChangedEvent(
    Guid RackId,
    string RepoOwner,
    string RepoName,
    int PullRequestNumber,
    string PullRequestUrl,
    string State,
    string? HeadSha,
    string ChecksConclusion,
    int? FailingChecksCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastCheckedAt,
    long Seq,
    Guid CorrelationId) : TopologyEvent;
