namespace Caisson.Ingestion.Git.ReadOnly;

/// <summary>Commit metadata observed for the configured branch's tip (story #62, AC1).</summary>
public sealed record GitCommitInfo(string Sha, string Author, DateTime CommitTimeUtc, string Message);

/// <summary>
/// A file path matched by the configured glob at a commit, with its Git blob size — returned by
/// <see cref="IGitRepositoryProvider.EnumerateFilesAsync"/> WITHOUT reading the blob's content, so a
/// caller can reject an oversized file before any content is materialised into memory (NFR8).
/// </summary>
public sealed record GitFileEntry(string Path, long SizeBytes);

/// <summary>Thrown by <see cref="IGitRepositoryProvider.ReadFileContentAsync"/> when a blob exceeds the caller's bound.</summary>
public sealed class GitFileTooLargeException(string path, long actualBytes, int maxBytes)
    : Exception($"'{path}' is {actualBytes} bytes, exceeding the {maxBytes}-byte bound.")
{
    /// <summary>The offending file's repository-relative path.</summary>
    public string Path { get; } = path;

    /// <summary>The blob's actual size in bytes.</summary>
    public long ActualBytes { get; } = actualBytes;

    /// <summary>The caller-supplied maximum, in bytes.</summary>
    public int MaxBytes { get; } = maxBytes;
}
