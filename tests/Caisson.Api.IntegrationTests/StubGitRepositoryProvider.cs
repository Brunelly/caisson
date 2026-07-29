using Caisson.Ingestion.Git.ReadOnly;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A minimal <see cref="IGitRepositoryProvider"/> stub for the API integration test host: no real Git
/// repository exists in this suite, so it returns a fixed empty commit with no matching files. This is
/// enough for the webhook/RBAC HTTP-contract tests, which only need <c>RunAsync</c> to complete (and
/// persist a run row), not to materialise any real desired state.
/// </summary>
public sealed class StubGitRepositoryProvider : IGitRepositoryProvider
{
    public Task<GitCommitInfo> GetLatestCommitAsync(string branch, string? credentialsRef, CancellationToken cancellationToken)
        => Task.FromResult(new GitCommitInfo("stub-sha", "stub-author", DateTime.UtcNow, "stub commit"));

    public Task<IReadOnlyList<GitFileEntry>> EnumerateFilesAsync(string commitSha, string pathGlob, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<GitFileEntry>>(Array.Empty<GitFileEntry>());

    public Task<string> ReadFileContentAsync(string commitSha, string path, int maxBytes, CancellationToken cancellationToken)
        => throw new InvalidOperationException("No files are ever enumerated by this stub.");
}
