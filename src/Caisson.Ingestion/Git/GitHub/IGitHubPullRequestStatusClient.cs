namespace Caisson.Ingestion.Git.GitHub;

/// <summary>The read-only PR snapshot needed to compute lifecycle state: raw <c>state</c>, <c>merged</c>, head SHA.</summary>
public sealed record GitHubPullRequestSnapshot(string State, bool Merged, string HeadSha);

/// <summary>A single GitHub check run for a ref, reduced to the fields the rollup needs (no secrets).</summary>
public sealed record GitHubCheckRun(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    string? DetailsUrl,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The check-runs listing for a ref: the reported total and the (bounded, one-request) page of runs.</summary>
public sealed record GitHubCheckRunsResult(int TotalCount, IReadOnlyList<GitHubCheckRun> CheckRuns);

/// <summary>
/// A capability-limited, <b>read-only</b> GitHub adapter for polling a pull request's current state and check
/// runs (story #173, Task #211a). Deliberately kept SEPARATE from story #172's write interface
/// <see cref="IGitHubPullRequestClient"/> so the <c>GitHubWriteBoundaryGuardTests</c> structural guard is not
/// touched and capabilities stay minimal: this interface exposes ONLY two GET operations and NO mutation.
/// Failures surface as a sanitized <see cref="GitHubStatusApiException"/> (stable category + rate-limit timing
/// only — never a token or response body, NFR2).
/// </summary>
public interface IGitHubPullRequestStatusClient
{
    /// <summary>GET <c>repos/{o}/{r}/pulls/{number}</c> — the PR's <c>state</c>, <c>merged</c> flag, and head SHA.</summary>
    Task<GitHubPullRequestSnapshot> GetPullRequestAsync(int number, CancellationToken cancellationToken);

    /// <summary>GET <c>repos/{o}/{r}/commits/{sha}/check-runs?per_page=100</c> — the check runs for a head SHA.</summary>
    Task<GitHubCheckRunsResult> GetCheckRunsForRefAsync(string headSha, CancellationToken cancellationToken);
}
