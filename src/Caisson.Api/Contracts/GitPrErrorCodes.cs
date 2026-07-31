namespace Caisson.Api.Contracts;

/// <summary>
/// Stable, operator-safe error codes surfaced to callers and support triage for the desired-state PR-creation
/// endpoint (story #172, AC6; NFR). Mirrors <c>Caisson.Orchestration.DriftApply.DriftApplyErrorCodes</c>:
/// UPPER_SNAKE names plus a <see cref="MessageFor"/> mapper to a fixed, secret-free message. Failure paths
/// surface the code (never a raw exception message, which could leak internal SQL/host/GitHub detail —
/// OWASP A05); the full exception is logged server-side only, keyed off the correlation id.
/// </summary>
public static class GitPrErrorCodes
{
    /// <summary>The request would push/commit to (or open a PR whose branch equals) the default branch (AC3).</summary>
    public const string PrOnlyGuardrailViolation = "PR_ONLY_GUARDRAIL_VIOLATION";

    /// <summary>Git credentials could not be retrieved from Key Vault / the configured provider (AC4).</summary>
    public const string GitCredentialsUnavailable = "GIT_CREDENTIALS_UNAVAILABLE";

    /// <summary>The git repository (owner/name/default branch) is not configured for this deployment.</summary>
    public const string GitRepoNotConfigured = "GIT_REPO_NOT_CONFIGURED";

    /// <summary>A GitHub API call failed (network/auth/rate-limit/unexpected status) with no PR created.</summary>
    public const string GitHubApiFailed = "GITHUB_API_FAILED";

    /// <summary>An unexpected error aborted PR creation.</summary>
    public const string UnexpectedError = "UNEXPECTED_ERROR";

    /// <summary>Maps a stable error code to its fixed, operator-safe message (no secret or internal detail).</summary>
    public static string MessageFor(string errorCode) => errorCode switch
    {
        PrOnlyGuardrailViolation =>
            "The request would write to the repository default branch. This API only creates feature "
            + "branches and pull requests; direct pushes and merges are refused.",
        GitCredentialsUnavailable => "Git credentials are currently unavailable; no pull request was created.",
        GitRepoNotConfigured => "No git repository is configured for pull-request creation.",
        GitHubApiFailed => "The GitHub API call failed; no pull request was created.",
        _ => "An unexpected error occurred while creating the pull request.",
    };
}
