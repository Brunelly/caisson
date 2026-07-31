namespace Caisson.Domain.Git;

/// <summary>
/// The durable idempotency + audit mapping from a rack candidate (identified by its
/// <see cref="CandidateFingerprint"/>) to the GitHub pull request that was created for it (story #172,
/// Task #206). A row is first inserted as a <see cref="GitPullRequestStatus.Open"/> <em>reservation</em>
/// (before any GitHub write) so concurrent identical requests collapse onto one PR via the filtered
/// partial-unique index on <c>(rack_id, candidate_fingerprint) WHERE status = 'Open'</c>; the reservation
/// winner then fills in the real <see cref="PullRequestNumber"/>/<see cref="PullRequestUrl"/>/
/// <see cref="CommitSha"/> with <see cref="MarkPublished"/>.
/// <para>
/// Deliberately a MUTABLE POCO with private setters and NOT <c>Caisson.Domain.Topology.IAppendOnly</c>
/// (modelled on <c>DesiredStateCandidateDiffCache</c>/<c>RackNetworkIntent</c>): the reservation must be
/// updatable to record the published PR and to close/merge the link as its GitHub state changes, which the
/// append-only <c>DbContext</c> sweep would otherwise block. A closed/merged link no longer participates in
/// reuse, so a new candidate with the same fingerprint after the prior PR closes correctly gets a fresh PR.
/// </para>
/// </summary>
public sealed class GitPullRequestLink
{
    /// <summary>Length of a lowercase SHA-256 hex digest (the candidate fingerprint).</summary>
    public const int FingerprintHexLength = 64;

    /// <summary>Maximum length of a GitHub repository owner/name segment.</summary>
    public const int MaxRepoSegmentLength = 128;

    /// <summary>Maximum length of a git branch (ref) name.</summary>
    public const int MaxBranchNameLength = 255;

    /// <summary>Maximum length of the stored pull-request URL.</summary>
    public const int MaxUrlLength = 512;

    /// <summary>Maximum length of a commit SHA (40 hex for SHA-1, 64 for SHA-256; bounded generously).</summary>
    public const int MaxCommitShaLength = 64;

    /// <summary>Maximum length of <see cref="CreatedBy"/>.</summary>
    public const int MaxActorLength = 256;

    /// <summary>Maximum length of the stored correlation id.</summary>
    public const int MaxCorrelationIdLength = 128;

    private GitPullRequestLink()
    {
        // EF Core materialization constructor.
        RepoOwner = null!;
        RepoName = null!;
        BranchName = null!;
        CandidateFingerprint = null!;
        CreatedBy = null!;
        CorrelationId = null!;
    }

    /// <summary>
    /// Creates a new <see cref="GitPullRequestStatus.Open"/> reservation for a candidate. The PR metadata
    /// (<see cref="PullRequestNumber"/>/<see cref="PullRequestUrl"/>/<see cref="CommitSha"/>) is filled in
    /// later by <see cref="MarkPublished"/> once the GitHub PR exists.
    /// </summary>
    public GitPullRequestLink(
        Guid id,
        Guid rackId,
        string repoOwner,
        string repoName,
        string branchName,
        string candidateFingerprint,
        string createdBy,
        DateTime createdAtUtc,
        string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoOwner);
        ArgumentException.ThrowIfNullOrEmpty(repoName);
        ArgumentException.ThrowIfNullOrEmpty(branchName);
        ArgumentException.ThrowIfNullOrEmpty(candidateFingerprint);
        ArgumentException.ThrowIfNullOrEmpty(createdBy);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);

        Id = id;
        RackId = rackId;
        RepoOwner = Bound(repoOwner, MaxRepoSegmentLength, nameof(repoOwner));
        RepoName = Bound(repoName, MaxRepoSegmentLength, nameof(repoName));
        BranchName = Bound(branchName, MaxBranchNameLength, nameof(branchName));
        CandidateFingerprint = Bound(candidateFingerprint, FingerprintHexLength, nameof(candidateFingerprint));
        Status = GitPullRequestStatus.Open;
        CreatedBy = Bound(createdBy, MaxActorLength, nameof(createdBy));
        CreatedAtUtc = createdAtUtc;
        LastCheckedAtUtc = createdAtUtc;
        CorrelationId = Bound(correlationId, MaxCorrelationIdLength, nameof(correlationId));
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack this PR link belongs to (rack-scoped; part of the idempotency key).</summary>
    public Guid RackId { get; private set; }

    /// <summary>The GitHub repository owner the PR was opened against.</summary>
    public string RepoOwner { get; private set; }

    /// <summary>The GitHub repository name the PR was opened against.</summary>
    public string RepoName { get; private set; }

    /// <summary>The feature branch created for this candidate (never the default branch — PR-only guardrail).</summary>
    public string BranchName { get; private set; }

    /// <summary>The opened pull request's number, or <c>null</c> while the reservation is unpublished.</summary>
    public int? PullRequestNumber { get; private set; }

    /// <summary>The opened pull request's URL, or <c>null</c> while the reservation is unpublished.</summary>
    public string? PullRequestUrl { get; private set; }

    /// <summary>The commit SHA of the desired-state file commit on the feature branch, or <c>null</c> if unpublished.</summary>
    public string? CommitSha { get; private set; }

    /// <summary>The SHA-256 of the candidate's canonical YAML (part of the idempotency key).</summary>
    public string CandidateFingerprint { get; private set; }

    /// <summary>The link lifecycle status; only <see cref="GitPullRequestStatus.Open"/> links are reused.</summary>
    public GitPullRequestStatus Status { get; private set; }

    /// <summary>The actor (user or service subject) who first requested this PR.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>When the reservation was first created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When the link's status/PR metadata was last reconciled.</summary>
    public DateTime LastCheckedAtUtc { get; private set; }

    /// <summary>The correlation id of the request that created the reservation (audit trace).</summary>
    public string CorrelationId { get; private set; }

    /// <summary>
    /// Records the real GitHub PR metadata onto a freshly-reserved link once the branch/commit/PR exist.
    /// Idempotent for a retry that re-reads the same PR: it simply overwrites with the same values.
    /// </summary>
    public void MarkPublished(int pullRequestNumber, string pullRequestUrl, string commitSha, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(pullRequestUrl);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);

        PullRequestNumber = pullRequestNumber;
        PullRequestUrl = Bound(pullRequestUrl, MaxUrlLength, nameof(pullRequestUrl));
        CommitSha = Bound(commitSha, MaxCommitShaLength, nameof(commitSha));
        LastCheckedAtUtc = atUtc;
    }

    /// <summary>
    /// Transitions the link to <see cref="GitPullRequestStatus.Closed"/>/<see cref="GitPullRequestStatus.Merged"/>
    /// so it no longer participates in idempotent reuse (a later identical candidate then gets a fresh PR).
    /// </summary>
    public void UpdateStatus(GitPullRequestStatus status, DateTime atUtc)
    {
        Status = status;
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
