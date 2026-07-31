namespace Caisson.Ingestion.Git.GitHub;

/// <summary>Repository metadata needed to target and guardrail a PR (only the default branch matters here).</summary>
public sealed record GitHubRepository(string DefaultBranch);

/// <summary>A branch/ref tip: its name and the commit SHA it points at.</summary>
public sealed record GitHubBranchRef(string Name, string CommitSha);

/// <summary>Existing file metadata on a ref — only the blob <see cref="Sha"/> is needed to update it.</summary>
public sealed record GitHubFileMetadata(string Path, string Sha);

/// <summary>The commit produced by writing the desired-state file onto a feature branch.</summary>
public sealed record GitHubCommit(string Sha);

/// <summary>An opened or discovered pull request.</summary>
public sealed record GitHubPullRequest(int Number, string HtmlUrl, string HeadRef, string BaseRef, string State);

/// <summary>
/// The capability-limited GitHub write adapter for desired-state PR creation (story #172, Task #204). Lives
/// in <c>Caisson.Ingestion.Git.GitHub</c> — a namespace DISTINCT from the read-only
/// <c>Caisson.Ingestion.Git.ReadOnly</c> ingestion path — so the read-only reflection guard stays scoped.
/// <para>
/// The PR-only guardrail is <b>structural</b>: this interface exposes ONLY the operations needed to read repo
/// metadata / a branch head / a file, create a NEW feature branch from the default head, commit the
/// desired-state file onto that feature branch, and open/find a PR targeting the default branch. It exposes
/// NO merge, force-push, push-to-default, delete-branch, or default-ref-update operation — enforced by a
/// reflection guard test (NFR4). Direct-default-branch writes are impossible because no method here can
/// target the default branch for a write (the branch to commit onto is always a caller-supplied feature
/// branch, re-checked by <see cref="PrOnlyGuardrail"/> before any write).
/// </para>
/// </summary>
public interface IGitHubPullRequestClient
{
    /// <summary>Reads repository metadata, including the authoritative default branch (guardrail authority + PR base).</summary>
    Task<GitHubRepository> GetRepositoryAsync(CancellationToken cancellationToken);

    /// <summary>Reads a branch's current tip (name + commit SHA), e.g. the default head to branch from.</summary>
    Task<GitHubBranchRef> GetBranchHeadAsync(string branch, CancellationToken cancellationToken);

    /// <summary>Reads a file's blob metadata on a ref, or <c>null</c> if the file does not exist on that ref.</summary>
    Task<GitHubFileMetadata?> GetFileMetadataAsync(string @ref, string path, CancellationToken cancellationToken);

    /// <summary>Creates a NEW feature ref pointing at <paramref name="fromCommitSha"/> (never the default branch).</summary>
    Task<GitHubBranchRef> CreateBranchAsync(string newBranchName, string fromCommitSha, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="contentText"/> to <paramref name="path"/> on <paramref name="branch"/> as a normal
    /// commit (create if <paramref name="existingFileSha"/> is null, else update). The branch is always a
    /// caller-supplied feature branch — never the default branch.
    /// </summary>
    Task<GitHubCommit> CommitFileAsync(
        string branch, string path, string contentText, string commitMessage, string? existingFileSha,
        CancellationToken cancellationToken);

    /// <summary>Opens a pull request from <paramref name="headBranch"/> targeting <paramref name="baseBranch"/> (the default branch).</summary>
    Task<GitHubPullRequest> OpenPullRequestAsync(
        string title, string body, string headBranch, string baseBranch, CancellationToken cancellationToken);

    /// <summary>Finds the open pull request whose head is <paramref name="headBranch"/>, or <c>null</c> if none.</summary>
    Task<GitHubPullRequest?> FindOpenPullRequestAsync(string headBranch, CancellationToken cancellationToken);
}
