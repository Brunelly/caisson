namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// Stable, sanitized reason codes recorded on a failed PR status poll (story #173, Task #211b). These are the
/// ONLY failure text ever persisted or logged for a poll failure — never a token, header, or response body.
/// </summary>
public static class GitPrPollFailureReasons
{
    /// <summary>401/403 — the GitHub credential was rejected or lacks permission.</summary>
    public const string CredentialsRejected = "CredentialsRejected";

    /// <summary>429 — GitHub rate-limited the request.</summary>
    public const string RateLimited = "RateLimited";

    /// <summary>The request exceeded the bounded per-request timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>A 5xx or transport error.</summary>
    public const string Transient = "Transient";

    /// <summary>404 — the PR or ref was not found on GitHub.</summary>
    public const string NotFound = "NotFound";

    /// <summary>An unclassified failure.</summary>
    public const string Unknown = "Unknown";

    /// <summary>Maps a sanitized GitHub failure category to its persisted reason code.</summary>
    public static string FromCategory(GitHubStatusFailureCategory category) => category switch
    {
        GitHubStatusFailureCategory.Unauthorized => CredentialsRejected,
        GitHubStatusFailureCategory.Forbidden => CredentialsRejected,
        GitHubStatusFailureCategory.RateLimited => RateLimited,
        GitHubStatusFailureCategory.Timeout => Timeout,
        GitHubStatusFailureCategory.Transient => Transient,
        GitHubStatusFailureCategory.NotFound => NotFound,
        _ => Unknown,
    };
}
