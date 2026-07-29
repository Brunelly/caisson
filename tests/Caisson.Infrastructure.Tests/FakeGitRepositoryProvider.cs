using System.Text;
using Caisson.Ingestion.Git.ReadOnly;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// An in-memory <see cref="IGitRepositoryProvider"/> test double for story #62's concurrency/
/// partial-accept tests: no real Git repository is ever touched. Represents the "current" commit as a
/// settable <see cref="NextCommit"/> plus a settable file map, so a test can simulate a sequence of
/// commits by mutating both between <c>RunAsync</c> calls.
/// </summary>
public sealed class FakeGitRepositoryProvider : IGitRepositoryProvider
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public GitCommitInfo NextCommit { get; set; } =
        new("sha-0", "author", DateTime.UtcNow, "initial commit");

    public Exception? FetchException { get; set; }

    public void SetFile(string path, string content) => _files[path] = content;

    public void RemoveFile(string path) => _files.Remove(path);

    public Task<GitCommitInfo> GetLatestCommitAsync(string branch, string? credentialsRef, CancellationToken cancellationToken)
        => FetchException is { } ex ? Task.FromException<GitCommitInfo>(ex) : Task.FromResult(NextCommit);

    public Task<IReadOnlyList<GitFileEntry>> EnumerateFilesAsync(string commitSha, string pathGlob, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<GitFileEntry>>(
            _files.Select(kv => new GitFileEntry(kv.Key, Encoding.UTF8.GetByteCount(kv.Value))).ToList());

    public Task<string> ReadFileContentAsync(string commitSha, string path, int maxBytes, CancellationToken cancellationToken)
    {
        var content = _files[path];
        var size = Encoding.UTF8.GetByteCount(content);
        if (size > maxBytes)
        {
            throw new GitFileTooLargeException(path, size, maxBytes);
        }

        return Task.FromResult(content);
    }
}
