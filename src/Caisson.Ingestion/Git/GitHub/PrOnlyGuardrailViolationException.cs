namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// Thrown by <see cref="PrOnlyGuardrail"/> when a request would write to the repository's default branch
/// (story #172, AC3). Defense-in-depth on top of the structurally merge-less
/// <see cref="IGitHubPullRequestClient"/>: it aborts BEFORE any git write so no side effect occurs. The
/// controller maps it to a 409 with the stable <c>PR_ONLY_GUARDRAIL_VIOLATION</c> error code. Carries no
/// secret text.
/// </summary>
public sealed class PrOnlyGuardrailViolationException : Exception
{
    public PrOnlyGuardrailViolationException(string message)
        : base(message)
    {
    }
}
