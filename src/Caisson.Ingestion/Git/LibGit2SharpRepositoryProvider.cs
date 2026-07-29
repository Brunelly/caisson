using System.Text.RegularExpressions;
using Caisson.Ingestion.Git.ReadOnly;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Git;

/// <summary>
/// The single concrete <see cref="IGitRepositoryProvider"/> implementation (ADR 0026): a LibGit2Sharp
/// library call rather than shelling to the <c>git</c> CLI, so there is zero command-injection surface.
/// Maintains a bounded local bare mirror of the configured repository under <see cref="_localMirrorPath"/>,
/// fetching (with prune) on demand rather than re-cloning on every poll tick.
/// </summary>
public sealed class LibGit2SharpRepositoryProvider : IGitRepositoryProvider
{
    private readonly string _repoUrl;
    private readonly string _localMirrorPath;
    private readonly ILogger<LibGit2SharpRepositoryProvider> _logger;

    // Serializes mirror creation/fetch only; concurrent reads against an already-fetched commit are
    // safe since they never mutate the local mirror.
    private readonly SemaphoreSlim _mirrorGate = new(1, 1);

    public LibGit2SharpRepositoryProvider(
        string repoUrl, string localMirrorPath, ILogger<LibGit2SharpRepositoryProvider> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoUrl);
        ArgumentException.ThrowIfNullOrEmpty(localMirrorPath);
        ArgumentNullException.ThrowIfNull(logger);

        _repoUrl = repoUrl;
        _localMirrorPath = localMirrorPath;
        _logger = logger;
    }

    public async Task<GitCommitInfo> GetLatestCommitAsync(
        string branch, string? credentialsRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);

        await EnsureMirrorUpToDateAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                using var repo = new Repository(_localMirrorPath);
                var branchRef = repo.Branches[$"origin/{branch}"]
                    ?? throw new InvalidOperationException(
                        $"Branch 'origin/{branch}' was not found after fetching '{_repoUrl}'.");
                var tip = branchRef.Tip;
                return new GitCommitInfo(tip.Sha, tip.Author.Name, tip.Author.When.UtcDateTime, tip.Message);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GitFileEntry>> EnumerateFilesAsync(
        string commitSha, string pathGlob, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(commitSha);
        ArgumentException.ThrowIfNullOrEmpty(pathGlob);

        return Task.Run(
            () =>
            {
                using var repo = new Repository(_localMirrorPath);
                var commit = LookupCommit(repo, commitSha);
                var pattern = GlobToRegex(pathGlob);

                var matches = new List<GitFileEntry>();
                WalkTree(commit.Tree, string.Empty, pattern, matches);
                return (IReadOnlyList<GitFileEntry>)matches;
            },
            cancellationToken);
    }

    public Task<string> ReadFileContentAsync(
        string commitSha, string path, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(commitSha);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return Task.Run(
            () =>
            {
                using var repo = new Repository(_localMirrorPath);
                var commit = LookupCommit(repo, commitSha);
                var entry = commit[path]
                    ?? throw new InvalidOperationException($"'{path}' was not found at commit '{commitSha}'.");

                if (entry.TargetType != TreeEntryTargetType.Blob || entry.Target is not Blob blob)
                {
                    throw new InvalidOperationException($"'{path}' is not a file at commit '{commitSha}'.");
                }

                // The blob's size is checked BEFORE its content is read into memory (NFR8).
                if (blob.Size > maxBytes)
                {
                    throw new GitFileTooLargeException(path, blob.Size, maxBytes);
                }

                return blob.GetContentText();
            },
            cancellationToken);
    }

    private async Task EnsureMirrorUpToDateAsync(CancellationToken cancellationToken)
    {
        await _mirrorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(EnsureMirrorUpToDateCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mirrorGate.Release();
        }
    }

    private void EnsureMirrorUpToDateCore()
    {
        if (!Repository.IsValid(_localMirrorPath))
        {
            _logger.LogInformation("Cloning desired-state repository mirror to {MirrorPath}.", _localMirrorPath);
            Directory.CreateDirectory(Directory.GetParent(_localMirrorPath)?.FullName ?? _localMirrorPath);
            Repository.Clone(_repoUrl, _localMirrorPath, new CloneOptions { IsBare = true });
            return;
        }

        using var repo = new Repository(_localMirrorPath);
        var remote = repo.Network.Remotes["origin"];
        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions { Prune = true }, logMessage: null);
    }

    private static Commit LookupCommit(Repository repo, string commitSha)
        => repo.Lookup<Commit>(commitSha)
            ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found in the local mirror.");

    private static void WalkTree(Tree tree, string prefix, Regex pattern, List<GitFileEntry> matches)
    {
        foreach (var entry in tree)
        {
            var relativePath = prefix.Length == 0 ? entry.Path : $"{prefix}/{entry.Name}";
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree when entry.Target is Tree subtree:
                    WalkTree(subtree, relativePath, pattern, matches);
                    break;
                case TreeEntryTargetType.Blob when entry.Target is Blob blob && pattern.IsMatch(relativePath):
                    matches.Add(new GitFileEntry(relativePath, blob.Size));
                    break;
            }
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        // Minimal glob support sufficient for the `desired-state/racks/*.yaml` path convention:
        // '*' matches any run of characters except '/', '**' matches across directories.
        var escaped = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                escaped.Append(".*");
                i++;
            }
            else if (glob[i] == '*')
            {
                escaped.Append("[^/]*");
            }
            else
            {
                escaped.Append(Regex.Escape(glob[i].ToString()));
            }
        }

        escaped.Append('$');
        return new Regex(escaped.ToString(), RegexOptions.Compiled);
    }
}
