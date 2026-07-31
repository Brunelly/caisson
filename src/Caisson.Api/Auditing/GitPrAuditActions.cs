namespace Caisson.Api.Auditing;

/// <summary>
/// The audit action strings for desired-state PR creation (story #172, AC6; data-model event types
/// <c>GIT_PR_CREATED</c>/<c>GIT_PR_REUSED</c>/<c>GIT_PR_REFUSED_PR_ONLY</c>/<c>GIT_PR_FAILED</c>). Uses the
/// repository's lowercase-dotted audit-action convention (e.g. <c>desired-state.pr-created</c>), one constant
/// per durable create/reuse/refuse/fail event, and exposes the story's UPPER_SNAKE event-type names for docs
/// and cross-referencing. Written through <c>IAuditEventWriter.WriteActionAsync</c>.
/// </summary>
public static class GitPrAuditActions
{
    /// <summary>A new pull request was created for a candidate (story event <c>GIT_PR_CREATED</c>).</summary>
    public const string Created = "git.pr.created";

    /// <summary>An existing open pull request was reused for an identical candidate (story event <c>GIT_PR_REUSED</c>).</summary>
    public const string Reused = "git.pr.reused";

    /// <summary>A request was refused by the PR-only guardrail (story event <c>GIT_PR_REFUSED_PR_ONLY</c>).</summary>
    public const string RefusedPrOnly = "git.pr.refused_pr_only";

    /// <summary>A pull-request creation attempt failed (story event <c>GIT_PR_FAILED</c>).</summary>
    public const string Failed = "git.pr.failed";
}
