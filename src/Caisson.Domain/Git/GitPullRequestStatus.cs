namespace Caisson.Domain.Git;

/// <summary>
/// The lifecycle status of a <see cref="GitPullRequestLink"/> (story #172, Task #206). Only
/// <see cref="Open"/> links participate in idempotent reuse — a <see cref="Closed"/> or <see cref="Merged"/>
/// link for the same rack/candidate fingerprint must NOT block a fresh PR (the filtered partial-unique index
/// is scoped to <c>Open</c>). Stored as its string name, mirroring the enum-as-string convention used by the
/// discovery/drift-apply job entities.
/// </summary>
public enum GitPullRequestStatus
{
    /// <summary>The pull request is open and is the reuse target for an identical candidate.</summary>
    Open,

    /// <summary>The pull request was closed without merging; it no longer blocks a fresh PR.</summary>
    Closed,

    /// <summary>The pull request was merged; it no longer blocks a fresh PR.</summary>
    Merged,
}
