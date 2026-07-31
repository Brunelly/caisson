using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Ingestion.Security;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// A minimal typed <see cref="HttpClient"/> (no Octokit) implementing the capability-limited, read-only
/// <see cref="IGitHubPullRequestStatusClient"/> (story #173, Task #211a). It reuses the exact plumbing
/// conventions of <c>GitHubRestPullRequestClient</c>: token from <see cref="IGitCredentialProvider"/> (set on
/// the request only, never logged), GitHub API-version + User-Agent headers, a bounded per-request timeout
/// (via <see cref="HttpClient.Timeout"/>), cancellation, and a light retry on transient 5xx. Every failure is
/// mapped to a redacted <see cref="GitHubStatusApiException"/> carrying only a stable category plus rate-limit
/// timing (Retry-After / X-RateLimit-Reset) — never auth headers or response bodies (NFR2).
/// </summary>
public sealed class GitHubRestPullRequestStatusClient : IGitHubPullRequestStatusClient
{
    private const string ApiVersion = "2022-11-28";
    private const string UserAgent = "Caisson-Control-Plane";
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GitHubClientSettings _settings;
    private readonly IGitCredentialProvider _credentials;
    private readonly ILogger<GitHubRestPullRequestStatusClient> _logger;

    public GitHubRestPullRequestStatusClient(
        HttpClient http,
        GitHubClientSettings settings,
        IGitCredentialProvider credentials,
        ILogger<GitHubRestPullRequestStatusClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string RepoPath => $"repos/{_settings.RepoOwner}/{_settings.RepoName}";

    /// <inheritdoc />
    public async Task<GitHubPullRequestSnapshot> GetPullRequestAsync(int number, CancellationToken cancellationToken)
    {
        var path = $"{RepoPath}/pulls/{number.ToString(CultureInfo.InvariantCulture)}";
        var dto = await SendAsync<PullRequestDto>(HttpMethod.Get, path, cancellationToken);
        return new GitHubPullRequestSnapshot(
            dto!.State ?? "open",
            dto.Merged ?? false,
            dto.Head?.Sha ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<GitHubCheckRunsResult> GetCheckRunsForRefAsync(string headSha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(headSha);

        var path = $"{RepoPath}/commits/{Uri.EscapeDataString(headSha)}/check-runs?per_page={GitHubChecksRollup.MaxCheckRuns}";
        var dto = await SendAsync<CheckRunsDto>(HttpMethod.Get, path, cancellationToken);

        var runs = (dto!.CheckRuns ?? new List<CheckRunDto>())
            .Select(r => new GitHubCheckRun(
                r.Id,
                r.Name ?? string.Empty,
                r.Status ?? string.Empty,
                r.Conclusion,
                r.DetailsUrl,
                r.StartedAt,
                r.CompletedAt))
            .ToList();

        // GitHub reports the true total even when a page is capped; fall back to the page size if absent.
        var totalCount = dto.TotalCount ?? runs.Count;
        return new GitHubCheckRunsResult(totalCount, runs);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetTokenAsync(cancellationToken);
        HttpResponseMessage? response = null;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                response?.Dispose();
                using var request = BuildRequest(method, path, credential);

                try
                {
                    response = await _http.SendAsync(request, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The bounded HttpClient.Timeout elapsed (not caller cancellation) → sanitized timeout.
                    _logger.LogWarning("GitHub read API {Method} {Path} timed out.", method.Method, path);
                    throw new GitHubStatusApiException(GitHubStatusFailureCategory.Timeout, method.Method, path);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("GitHub read API {Method} {Path} transport error.", method.Method, path);
                    throw new GitHubStatusApiException(
                        GitHubStatusFailureCategory.Transient, method.Method, path, innerException: ex);
                }

                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "GitHub read API {Method} {Path} returned transient HTTP {Status}; retrying ({Attempt}/{Max}).",
                        method.Method, path, (int)response.StatusCode, attempt, MaxAttempts);
                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                    continue;
                }

                break;
            }

            if (!response!.IsSuccessStatusCode)
            {
                throw BuildFailure(method, path, response);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private GitHubStatusApiException BuildFailure(HttpMethod method, string path, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var (retryAfter, reset) = ReadRateLimitTiming(response);
        var category = status switch
        {
            401 => GitHubStatusFailureCategory.Unauthorized,
            // GitHub commonly signals primary/secondary rate limiting with 403 + X-RateLimit-Remaining: 0
            // and/or Retry-After, not only 429 (NFR1). Classify those as RateLimited so ComputeNextPollAfter
            // honours the reset window instead of applying generic backoff; a plain 403 stays Forbidden.
            403 when IsRateLimitSignalled(response, retryAfter) => GitHubStatusFailureCategory.RateLimited,
            403 => GitHubStatusFailureCategory.Forbidden,
            429 => GitHubStatusFailureCategory.RateLimited,
            404 => GitHubStatusFailureCategory.NotFound,
            >= 500 => GitHubStatusFailureCategory.Transient,
            _ => GitHubStatusFailureCategory.Unknown,
        };

        // Redaction: log status + method + path only — never the response body or Authorization header (NFR2).
        _logger.LogError(
            "GitHub read API {Method} {Path} failed with HTTP {Status} ({Category}).",
            method.Method, path, status, category);

        return new GitHubStatusApiException(category, method.Method, path, status, retryAfter, reset);
    }

    /// <summary>
    /// Whether a 403 carries a rate-limit signal — an exhausted primary budget (<c>X-RateLimit-Remaining: 0</c>)
    /// or an explicit <c>Retry-After</c> (secondary rate limiting) — so it is treated as RateLimited rather than
    /// a credentials rejection.
    /// </summary>
    private static bool IsRateLimitSignalled(HttpResponseMessage response, TimeSpan? retryAfter)
    {
        if (retryAfter.HasValue)
        {
            return true;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
        {
            return remaining <= 0;
        }

        return false;
    }

    /// <summary>Parses <c>Retry-After</c> (seconds or HTTP-date) and <c>X-RateLimit-Reset</c> (unix seconds).</summary>
    private static (TimeSpan? RetryAfter, DateTimeOffset? Reset) ReadRateLimitTiming(HttpResponseMessage response)
    {
        TimeSpan? retryAfter = null;
        var ra = response.Headers.RetryAfter;
        if (ra is not null)
        {
            if (ra.Delta.HasValue)
            {
                retryAfter = ra.Delta;
            }
            else if (ra.Date.HasValue)
            {
                var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                retryAfter = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
        }

        DateTimeOffset? reset = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            var raw = values.FirstOrDefault();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
        }

        return (retryAfter, reset);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, GitHubCredential credential)
    {
        var request = new HttpRequestMessage(method, BuildUri(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        // The token is set on the outbound request only and is never logged (redaction, NFR2).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Reveal());
        return request;
    }

    private Uri BuildUri(string path)
    {
        var baseUri = _settings.ApiBaseUrl.TrimEnd('/');
        return new Uri($"{baseUri}/{path}");
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(100 * attempt);

    private sealed record PullRequestDto(
        string? State,
        bool? Merged,
        PullHeadDto? Head);

    private sealed record PullHeadDto(string? Sha);

    private sealed record CheckRunsDto(
        [property: JsonPropertyName("total_count")] int? TotalCount,
        [property: JsonPropertyName("check_runs")] List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(
        long Id,
        string? Name,
        string? Status,
        string? Conclusion,
        [property: JsonPropertyName("details_url")] string? DetailsUrl,
        [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
        [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);
}
