namespace Caisson.Ingestion.Git.ReadOnly;

/// <summary>
/// Read-only access to the configured desired-state Git repository. This interface — and every type in
/// the <see cref="ReadOnly"/> namespace — is the safety boundary for story #62's "only reads Git and
/// stores results; no device credentials or device operations" scope: no method here writes to the
/// repository, enforced by a reflection guard test that fails the build if a mutating method name ever
/// appears here (mirroring <c>Caisson.Drivers.Abstractions.ReadOnly</c>'s driver safety boundary).
/// </summary>
public interface IGitRepositoryProvider
{
    /// <summary>
    /// Fetches the latest commit for <paramref name="branch"/>. <paramref name="credentialsRef"/> is an
    /// opaque, currently-unused reference for a future private-repo story (ADR 0026); M1 assumes
    /// unauthenticated HTTPS access.
    /// </summary>
    Task<GitCommitInfo> GetLatestCommitAsync(
        string branch, string? credentialsRef, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates files matching <paramref name="pathGlob"/> at <paramref name="commitSha"/>, returning
    /// each match's path and blob size WITHOUT reading its content.
    /// </summary>
    Task<IReadOnlyList<GitFileEntry>> EnumerateFilesAsync(
        string commitSha, string pathGlob, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one file's content at <paramref name="commitSha"/>. The blob's size is checked against
    /// <paramref name="maxBytes"/> BEFORE any content is read into memory (NFR8); an oversized blob
    /// throws <see cref="GitFileTooLargeException"/> rather than being materialised.
    /// </summary>
    Task<string> ReadFileContentAsync(
        string commitSha, string path, int maxBytes, CancellationToken cancellationToken);
}
