using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Security;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A <see cref="TimeProvider"/> that returns real time until <see cref="Pinned"/> is set, then returns the
/// pinned instant. Lets the deterministic PR-only-guardrail-collision test compute the exact generated branch
/// name; unpinned (the default) it behaves exactly like <see cref="TimeProvider.System"/>, so no other test is
/// affected.
/// </summary>
public sealed class MutableTimeProvider : TimeProvider
{
    /// <summary>When set, all reads return this instant; when null, real system time is returned.</summary>
    public DateTimeOffset? Pinned { get; set; }

    public override DateTimeOffset GetUtcNow() => Pinned ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// An in-memory <see cref="IGitCredentialProvider"/> for the API suite — never touches Azure. Tracks how many
/// times a token was requested (so a reuse path can be asserted to make no credential call) and can be told to
/// fail closed to exercise the <c>GIT_CREDENTIALS_UNAVAILABLE</c> path.
/// </summary>
public sealed class FakeGitCredentialProvider : IGitCredentialProvider
{
    private int _calls;

    /// <summary>The number of times a credential was requested since the last <see cref="Reset"/>.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>When true, <see cref="GetTokenAsync"/> throws <see cref="GitCredentialUnavailableException"/>.</summary>
    public bool FailUnavailable { get; set; }

    public void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        FailUnavailable = false;
    }

    public Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (FailUnavailable)
        {
            throw new GitCredentialUnavailableException("Fake credential provider configured to fail.");
        }

        return Task.FromResult(new GitHubCredential("fake-pat-token"));
    }
}

/// <summary>
/// An in-memory <see cref="IGitHubPullRequestClient"/> for the API suite — no real network. It calls the
/// injected <see cref="FakeGitCredentialProvider"/> at the first repository read (mimicking the real client's
/// per-call auth) so credential-call counts are meaningful, records call counts (so the N-concurrent →
/// 1-PR invariant and the "reuse makes zero GitHub calls" property are assertable), captures the last opened
/// PR's title/body, and can simulate a GitHub API failure. It exposes NO merge/force/push-to-default method —
/// the interface makes that structurally impossible.
/// </summary>
public sealed class FakeGitHubPullRequestClient : IGitHubPullRequestClient
{
    private readonly FakeGitCredentialProvider _credentials;
    private int _prNumber = 1000;
    private int _getRepositoryCalls;
    private int _createBranchCalls;
    private int _commitFileCalls;
    private int _openPullRequestCalls;

    public FakeGitHubPullRequestClient(FakeGitCredentialProvider credentials) => _credentials = credentials;

    /// <summary>The default branch reported by <see cref="GetRepositoryAsync"/> (guardrail authority + PR base).</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>The commit SHA reported as the default-branch head.</summary>
    public string BaseHeadSha { get; set; } = "0000000000000000000000000000000000000001";

    /// <summary>The file metadata returned by <see cref="GetFileMetadataAsync"/> (null → file absent, a create).</summary>
    public GitHubFileMetadata? ExistingFile { get; set; }

    /// <summary>When true, <see cref="OpenPullRequestAsync"/> throws a <see cref="GitHubApiException"/>.</summary>
    public bool FailOnOpen { get; set; }

    /// <summary>The HTTP status simulated when <see cref="FailOnOpen"/> is set.</summary>
    public int FailOnOpenStatus { get; set; } = 500;

    public int GetRepositoryCalls => Volatile.Read(ref _getRepositoryCalls);
    public int CreateBranchCalls => Volatile.Read(ref _createBranchCalls);
    public int CommitFileCalls => Volatile.Read(ref _commitFileCalls);
    public int OpenPullRequestCalls => Volatile.Read(ref _openPullRequestCalls);

    public string? LastTitle { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastCommitContent { get; private set; }
    public string? LastCommitPath { get; private set; }

    public void Reset()
    {
        Interlocked.Exchange(ref _getRepositoryCalls, 0);
        Interlocked.Exchange(ref _createBranchCalls, 0);
        Interlocked.Exchange(ref _commitFileCalls, 0);
        Interlocked.Exchange(ref _openPullRequestCalls, 0);
        Interlocked.Exchange(ref _prNumber, 1000);
        DefaultBranch = "main";
        ExistingFile = null;
        FailOnOpen = false;
        FailOnOpenStatus = 500;
        LastTitle = null;
        LastBody = null;
        LastCommitContent = null;
        LastCommitPath = null;
    }

    public async Task<GitHubRepository> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getRepositoryCalls);
        // Mimic the real client requiring a credential for every GitHub call.
        await _credentials.GetTokenAsync(cancellationToken);
        return new GitHubRepository(DefaultBranch);
    }

    public Task<GitHubBranchRef> GetBranchHeadAsync(string branch, CancellationToken cancellationToken)
        => Task.FromResult(new GitHubBranchRef(branch, BaseHeadSha));

    public Task<GitHubFileMetadata?> GetFileMetadataAsync(string @ref, string path, CancellationToken cancellationToken)
        => Task.FromResult(ExistingFile);

    public Task<GitHubBranchRef> CreateBranchAsync(string newBranchName, string fromCommitSha, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _createBranchCalls);
        return Task.FromResult(new GitHubBranchRef(newBranchName, fromCommitSha));
    }

    public Task<GitHubCommit> CommitFileAsync(
        string branch, string path, string contentText, string commitMessage, string? existingFileSha,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _commitFileCalls);
        LastCommitContent = contentText;
        LastCommitPath = path;
        return Task.FromResult(new GitHubCommit("commit" + Guid.NewGuid().ToString("N")[..12]));
    }

    public Task<GitHubPullRequest> OpenPullRequestAsync(
        string title, string body, string headBranch, string baseBranch, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openPullRequestCalls);
        if (FailOnOpen)
        {
            throw new GitHubApiException(FailOnOpenStatus, "POST", "/pulls");
        }

        LastTitle = title;
        LastBody = body;
        var number = Interlocked.Increment(ref _prNumber);
        return Task.FromResult(new GitHubPullRequest(
            number, $"https://github.test/{headBranch}/pull/{number}", headBranch, baseBranch, "open"));
    }

    public Task<GitHubPullRequest?> FindOpenPullRequestAsync(string headBranch, CancellationToken cancellationToken)
        => Task.FromResult<GitHubPullRequest?>(null);
}
