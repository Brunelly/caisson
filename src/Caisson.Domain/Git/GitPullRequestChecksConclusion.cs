namespace Caisson.Domain.Git;

/// <summary>
/// The rolled-up conclusion of a pull request's GitHub check runs for its head SHA (story #173, Task #210).
/// Computed deterministically by <c>GitHubChecksRollup.Summarize</c> with worst-wins precedence and stored as
/// its string name on <see cref="GitPullRequestStatusRecord"/>, mirroring the enum-as-string convention used
/// by the sibling git/job entities. Values follow the story's enumerated checks vocabulary.
/// </summary>
public enum GitPullRequestChecksConclusion
{
    /// <summary>All usable check runs completed successfully (or were neutral/skipped).</summary>
    Success,

    /// <summary>At least one check run failed.</summary>
    Failure,

    /// <summary>The worst usable conclusion was neutral.</summary>
    Neutral,

    /// <summary>At least one check run was cancelled.</summary>
    Cancelled,

    /// <summary>The worst usable conclusion was skipped.</summary>
    Skipped,

    /// <summary>At least one check run timed out.</summary>
    TimedOut,

    /// <summary>At least one check run requires a manual action.</summary>
    ActionRequired,

    /// <summary>At least one check run's result is stale.</summary>
    Stale,

    /// <summary>At least one check run is still queued/in-progress (no terminal conclusion yet).</summary>
    Pending,

    /// <summary>No usable check runs were reported, or the conclusion could not be determined.</summary>
    Unknown,
}
