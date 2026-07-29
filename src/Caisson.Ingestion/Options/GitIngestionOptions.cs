using Caisson.Domain.DesiredState;

namespace Caisson.Ingestion.Options;

/// <summary>
/// Control-plane configuration for Git-backed desired-state ingestion (story #62), config-bound under
/// <see cref="SectionName"/> — mirrors <c>RackDefinitionOptions</c>/<c>DiscoveryOrchestrationOptions</c>.
/// Deliberately carries NO secret-shaped field: the webhook secret resolves through
/// <c>IGitIngestionSecretsResolver</c> (env-var-backed, ADR 0026), never through this POCO, so it can
/// never accidentally be serialized/logged alongside these settings.
/// </summary>
public sealed class GitIngestionOptions
{
    /// <summary>Configuration section name (<c>GitIngestion</c>).</summary>
    public const string SectionName = "GitIngestion";

    /// <summary>Whether polling and webhook-triggered ingestion are active.</summary>
    public bool Enabled { get; set; }

    /// <summary>The single configured repository URL (Q1: one repo/branch per installation).</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>The branch desired-state YAML is read from.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Poll interval, in seconds, for the scheduled fetch (AC1).</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Path glob matching one desired-state rack file per rack (AC1 example).</summary>
    public string PathGlob { get; set; } = "desired-state/racks/*.yaml";

    /// <summary>
    /// Local filesystem path for the bounded bare Git mirror
    /// (<see cref="Git.LibGit2SharpRepositoryProvider"/>).
    /// </summary>
    public string LocalMirrorPath { get; set; } = "./data/git-ingestion-mirror";

    /// <summary>Per-file byte bound, checked before content is read (NFR8). Defaults to the schema bound.</summary>
    public int MaxFileBytes { get; set; } = DesiredStateSchema.MaxFileBytes;

    /// <summary>Maximum number of matching rack files processed per commit (NFR4).</summary>
    public int MaxFilesPerCommit { get; set; } = DesiredStateSchema.MaxFilesPerCommit;
}
