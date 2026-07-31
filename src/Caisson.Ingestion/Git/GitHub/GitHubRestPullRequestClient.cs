using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Ingestion.Security;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>Non-secret connection settings for the GitHub write client (mapped from <c>GitHubOptions</c> by DI).</summary>
public sealed record GitHubClientSettings(string ApiBaseUrl, string RepoOwner, string RepoName);

/// <summary>
/// A typed failure raised when a GitHub REST call fails after retries. Carries the HTTP status code and the
/// method+path (never the Authorization header or the response body — both are redacted, NFR2). The publisher
/// maps it to the stable <c>GITHUB_API_FAILED</c> error code.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(int statusCode, string method, string path)
        : base($"GitHub API call {method} {path} failed with HTTP {statusCode}.")
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code GitHub returned (e.g. 401/403/404/409/422/429/5xx).</summary>
    public int StatusCode { get; }
}

/// <summary>
/// A minimal typed <see cref="HttpClient"/> over the BCL client (no Octokit) for the capability-limited
/// GitHub write path (story #172, Task #204), modelled on <c>Caisson.Drivers.Redfish.Transport.RedfishClient</c>.
/// It sets the GitHub API-version + User-Agent headers, authenticates each request with a bearer token from
/// <see cref="IGitCredentialProvider"/> (the token is set on the request only, never logged), applies
/// cancellation, a bounded per-request timeout (via the injected <see cref="HttpClient.Timeout"/>), and a
/// light retry on 429/5xx. GitHub error responses map to <see cref="GitHubApiException"/>; the Authorization
/// header and response bodies are never written to logs.
/// </summary>
public sealed class GitHubRestPullRequestClient : IGitHubPullRequestClient
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
    private readonly ILogger<GitHubRestPullRequestClient> _logger;

    public GitHubRestPullRequestClient(
        HttpClient http,
        GitHubClientSettings settings,
        IGitCredentialProvider credentials,
        ILogger<GitHubRestPullRequestClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string RepoPath => $"repos/{_settings.RepoOwner}/{_settings.RepoName}";

    /// <inheritdoc />
    public async Task<GitHubRepository> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        var dto = await SendAsync<RepositoryDto>(HttpMethod.Get, RepoPath, body: null, cancellationToken);
        return new GitHubRepository(dto!.DefaultBranch ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<GitHubBranchRef> GetBranchHeadAsync(string branch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        var path = $"{RepoPath}/branches/{Uri.EscapeDataString(branch)}";
        var dto = await SendAsync<BranchDto>(HttpMethod.Get, path, body: null, cancellationToken);
        return new GitHubBranchRef(dto!.Name ?? branch, dto.Commit?.Sha ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<GitHubFileMetadata?> GetFileMetadataAsync(
        string @ref, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(@ref);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var url = $"{RepoPath}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(@ref)}";
        var (status, dto) = await TrySendAsync<ContentsDto>(HttpMethod.Get, url, body: null, cancellationToken);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return new GitHubFileMetadata(dto!.Path ?? path, dto.Sha ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<GitHubBranchRef> CreateBranchAsync(
        string newBranchName, string fromCommitSha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(newBranchName);
        ArgumentException.ThrowIfNullOrEmpty(fromCommitSha);

        var body = new Dictionary<string, object?>
        {
            ["ref"] = $"refs/heads/{newBranchName}",
            ["sha"] = fromCommitSha,
        };
        var dto = await SendAsync<RefDto>(HttpMethod.Post, $"{RepoPath}/git/refs", body, cancellationToken);
        return new GitHubBranchRef(newBranchName, dto!.Object?.Sha ?? fromCommitSha);
    }

    /// <inheritdoc />
    public async Task<GitHubCommit> CommitFileAsync(
        string branch, string path, string contentText, string commitMessage, string? existingFileSha,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contentText);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        var body = new Dictionary<string, object?>
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(contentText)),
            ["branch"] = branch,
        };
        if (!string.IsNullOrEmpty(existingFileSha))
        {
            // Updating an existing file requires its current blob sha; creating a new file omits it.
            body["sha"] = existingFileSha;
        }

        var dto = await SendAsync<ContentsWriteDto>(
            HttpMethod.Put, $"{RepoPath}/contents/{EscapePath(path)}", body, cancellationToken);
        return new GitHubCommit(dto!.Commit?.Sha ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<GitHubPullRequest> OpenPullRequestAsync(
        string title, string body, string headBranch, string baseBranch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(headBranch);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);

        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["body"] = body,
            ["head"] = headBranch,
            ["base"] = baseBranch,
        };
        var dto = await SendAsync<PullRequestDto>(HttpMethod.Post, $"{RepoPath}/pulls", payload, cancellationToken);
        return ToPullRequest(dto!);
    }

    private static GitHubPullRequest ToPullRequest(PullRequestDto dto)
        => new(dto.Number, dto.HtmlUrl ?? string.Empty, dto.Head?.Ref ?? string.Empty,
            dto.Base?.Ref ?? string.Empty, dto.State ?? "open");

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var (_, dto) = await TrySendAsync<T>(method, path, body, cancellationToken, throwOnNotFound: true);
        return dto;
    }

    /// <summary>
    /// Sends one request with a light retry on 429/5xx. Returns the terminal status and deserialized body.
    /// Non-success statuses (other than a tolerated 404 when <paramref name="throwOnNotFound"/> is false)
    /// throw <see cref="GitHubApiException"/> with no body/secret detail.
    /// </summary>
    private async Task<(HttpStatusCode Status, T? Body)> TrySendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        bool throwOnNotFound = false)
    {
        var credential = await _credentials.GetTokenAsync(cancellationToken);
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            response?.Dispose();
            using var request = BuildRequest(method, path, body, credential);
            response = await _http.SendAsync(request, cancellationToken);

            if (ShouldRetry(method, response.StatusCode) && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "GitHub API {Method} {Path} returned transient HTTP {Status}; retrying (attempt {Attempt}/{Max}).",
                    method.Method, path, (int)response.StatusCode, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            break;
        }

        using (response)
        {
            if (response!.StatusCode == HttpStatusCode.NotFound && !throwOnNotFound)
            {
                return (HttpStatusCode.NotFound, default);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Redaction: log status + method + path only — never the response body or Authorization header.
                _logger.LogError(
                    "GitHub API {Method} {Path} failed with HTTP {Status}.",
                    method.Method, path, (int)response.StatusCode);
                throw new GitHubApiException((int)response.StatusCode, method.Method, path);
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return (response.StatusCode, default);
            }

            var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return (response.StatusCode, payload);
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body, GitHubCredential credential)
    {
        var request = new HttpRequestMessage(method, BuildUri(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        // The token is set on the outbound request only and is never logged (redaction, NFR2).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Reveal());

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private Uri BuildUri(string path)
    {
        var baseUri = _settings.ApiBaseUrl.TrimEnd('/');
        return new Uri($"{baseUri}/{path}");
    }

    /// <summary>
    /// Decides whether a non-success status is safely retryable for this verb. A 429 (rate-limited) means the
    /// request was rejected and never processed, so it is safe to retry for any verb. A 5xx means the server
    /// received — and may have partially applied — the request, so it is retried ONLY for idempotent verbs:
    /// retrying a non-idempotent POST (create-ref / open-PR) risks a duplicate or a 422 "already exists" on a
    /// request that in fact succeeded, surfacing a spurious GITHUB_API_FAILED for a partial success.
    /// </summary>
    private static bool ShouldRetry(HttpMethod method, HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return (int)status >= 500 && IsIdempotent(method);
    }

    private static bool IsIdempotent(HttpMethod method)
        => method == HttpMethod.Get || method == HttpMethod.Put
            || method == HttpMethod.Head || method == HttpMethod.Delete;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(100 * attempt);

    // The contents API path may contain slashes that must be preserved as path separators (not escaped).
    private static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private sealed record RepositoryDto([property: JsonPropertyName("default_branch")] string? DefaultBranch);

    private sealed record BranchDto(string? Name, CommitRefDto? Commit);

    private sealed record CommitRefDto(string? Sha);

    private sealed record ContentsDto(string? Path, string? Sha);

    private sealed record RefDto([property: JsonPropertyName("object")] RefObjectDto? Object);

    private sealed record RefObjectDto(string? Sha);

    private sealed record ContentsWriteDto(CommitRefDto? Commit);

    private sealed record PullRequestDto(
        int Number,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        PullRefDto? Head,
        PullRefDto? Base,
        string? State);

    private sealed record PullRefDto([property: JsonPropertyName("ref")] string? Ref);
}
