namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// A stable, sanitized classification of a GitHub read-API failure (story #173, Task #211a). The poller keys
/// its backoff/audit decisions off this category and NEVER off a raw HTTP body or auth detail.
/// </summary>
public enum GitHubStatusFailureCategory
{
    /// <summary>401 — the credential was rejected (invalid/expired token).</summary>
    Unauthorized,

    /// <summary>403 — the credential is valid but lacks permission (or a secondary rate limit).</summary>
    Forbidden,

    /// <summary>429 — the primary rate limit was hit; honour the rate-limit timing.</summary>
    RateLimited,

    /// <summary>404 — the pull request / ref was not found on GitHub.</summary>
    NotFound,

    /// <summary>The request exceeded the bounded per-request timeout.</summary>
    Timeout,

    /// <summary>A 5xx or transport error — transient, safe to retry with backoff.</summary>
    Transient,

    /// <summary>An unclassified failure.</summary>
    Unknown,
}

/// <summary>
/// A redacted failure raised when a GitHub read call fails after retries (story #173, Task #211a). Carries ONLY
/// a stable <see cref="Category"/>, the HTTP status code (when there was a response), and rate-limit timing
/// metadata parsed from <c>Retry-After</c> / <c>X-RateLimit-Reset</c> — never the Authorization header, the
/// token, or the response body (NFR2). The poller maps <see cref="Category"/> to a sanitized reason code and
/// uses <see cref="RetryAfter"/>/<see cref="RateLimitResetUtc"/> to schedule <c>NextPollAfterUtc</c>.
/// </summary>
public sealed class GitHubStatusApiException : Exception
{
    public GitHubStatusApiException(
        GitHubStatusFailureCategory category,
        string method,
        string path,
        int? statusCode = null,
        TimeSpan? retryAfter = null,
        DateTimeOffset? rateLimitResetUtc = null,
        Exception? innerException = null)
        : base($"GitHub read API call {method} {path} failed ({category}).", innerException)
    {
        Category = category;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        RateLimitResetUtc = rateLimitResetUtc;
    }

    /// <summary>The stable, sanitized failure classification.</summary>
    public GitHubStatusFailureCategory Category { get; }

    /// <summary>The HTTP status code, or <c>null</c> for a transport/timeout failure with no response.</summary>
    public int? StatusCode { get; }

    /// <summary>The <c>Retry-After</c> delay GitHub asked for, if present.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The <c>X-RateLimit-Reset</c> instant (UTC) the window resets at, if present.</summary>
    public DateTimeOffset? RateLimitResetUtc { get; }
}
