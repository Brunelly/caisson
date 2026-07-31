namespace Caisson.Domain.Git;

/// <summary>
/// The audit action strings for PR status transitions (story #173, Task #214). Follows the repository's
/// lowercase-dotted audit-action convention (as <c>GitPrAuditActions</c> does for creation) and complements
/// the story data-model event types <c>GitPullRequestStatusChanged</c>/<c>GitPullRequestChecksChanged</c>. One
/// constant per meaningful transition kind; written directly to the append-only <c>topology_audit_event</c>
/// table by the poller's transition choke point.
/// </summary>
public static class GitPrStatusAuditActions
{
    /// <summary>The PR lifecycle state changed (e.g. Open→Merged / Open→Closed).</summary>
    public const string StatusChanged = "git.pr.status_changed";

    /// <summary>The rolled-up checks conclusion changed (e.g. Pending→Success / Success→Failure).</summary>
    public const string ChecksChanged = "git.pr.checks_changed";

    /// <summary>The audit target type for a PR status/checks transition.</summary>
    public const string TargetType = "git-pull-request";
}
