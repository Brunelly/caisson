using System.Net;
using System.Net.Http.Headers;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Ingestion.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubRestPullRequestStatusClient"/> (story #173, Task #211a) over a stubbed
/// <see cref="HttpMessageHandler"/>: the two GET paths, state mapping via the explicit <c>merged</c> field,
/// and that 401/403/429/timeout map to a sanitized <see cref="GitHubStatusApiException"/> carrying only a
/// category + rate-limit timing (never a token or the response body).
/// </summary>
public sealed class GitHubPullRequestStatusClientTests
{
    private static readonly GitHubClientSettings Settings = new("https://api.github.test", "octo", "repo");

    [Fact]
    public async Task GetPullRequest_uses_expected_path_and_maps_merged_true_to_merged()
    {
        var handler = new StubHandler(_ => Ok(
            "{\"state\":\"closed\",\"merged\":true,\"head\":{\"sha\":\"abc123\"}}"));
        var client = NewClient(handler);

        var snapshot = await client.GetPullRequestAsync(42, default);

        snapshot.Merged.Should().BeTrue();
        snapshot.State.Should().Be("closed");
        snapshot.HeadSha.Should().Be("abc123");
        handler.Requests.Should().ContainSingle()
            .Which.Path.Should().Be("/repos/octo/repo/pulls/42");
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer test-token");
    }

    [Fact]
    public async Task GetCheckRuns_uses_per_page_100_and_maps_runs()
    {
        var handler = new StubHandler(_ => Ok(
            "{\"total_count\":1,\"check_runs\":[{\"id\":9,\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"success\",\"details_url\":\"https://gh/run/9\"}]}"));
        var client = NewClient(handler);

        var result = await client.GetCheckRunsForRefAsync("abc123", default);

        result.TotalCount.Should().Be(1);
        result.CheckRuns.Should().ContainSingle();
        result.CheckRuns[0].Name.Should().Be("build");
        var request = handler.Requests.Should().ContainSingle().Which;
        request.Path.Should().Be("/repos/octo/repo/commits/abc123/check-runs");
        request.Query.Should().Contain("per_page=100");
    }

    [Fact]
    public async Task Merged_false_open_state_maps_to_open()
    {
        var handler = new StubHandler(_ => Ok("{\"state\":\"open\",\"merged\":false,\"head\":{\"sha\":\"x\"}}"));
        var client = NewClient(handler);

        var snapshot = await client.GetPullRequestAsync(1, default);

        snapshot.State.Should().Be("open");
        snapshot.Merged.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitHubStatusFailureCategory.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GitHubStatusFailureCategory.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, GitHubStatusFailureCategory.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, GitHubStatusFailureCategory.NotFound)]
    public async Task Error_responses_map_to_a_sanitized_category(HttpStatusCode status, GitHubStatusFailureCategory expected)
    {
        var handler = new StubHandler(_ => (status, "{\"message\":\"secret-detail\"}", null));
        var client = NewClient(handler);

        var act = async () => await client.GetPullRequestAsync(1, default);

        var ex = (await act.Should().ThrowAsync<GitHubStatusApiException>()).Which;
        ex.Category.Should().Be(expected);
        ex.StatusCode.Should().Be((int)status);
        // No response body / secret detail leaks into the exception message.
        ex.Message.Should().NotContain("secret-detail");
    }

    [Fact]
    public async Task Rate_limited_response_surfaces_retry_after_and_reset_timing()
    {
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var handler = new StubHandler(_ => (
            HttpStatusCode.TooManyRequests,
            "{}",
            new Dictionary<string, string>
            {
                ["Retry-After"] = "120",
                ["X-RateLimit-Reset"] = resetUnix.ToString(),
            }));
        var client = NewClient(handler);

        var act = async () => await client.GetPullRequestAsync(1, default);

        var ex = (await act.Should().ThrowAsync<GitHubStatusApiException>()).Which;
        ex.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        ex.RateLimitResetUtc!.Value.ToUnixTimeSeconds().Should().Be(resetUnix);
    }

    [Fact]
    public async Task Forbidden_with_exhausted_rate_limit_maps_to_rate_limited_and_honours_reset()
    {
        // GitHub signals a hit primary rate limit with 403 + X-RateLimit-Remaining: 0 (not 429). It must be
        // treated as RateLimited so the reset timing is respected rather than generic credentials backoff (NFR1).
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var handler = new StubHandler(_ => (
            HttpStatusCode.Forbidden,
            "{\"message\":\"API rate limit exceeded\"}",
            new Dictionary<string, string>
            {
                ["X-RateLimit-Remaining"] = "0",
                ["X-RateLimit-Reset"] = resetUnix.ToString(),
            }));
        var client = NewClient(handler);

        var act = async () => await client.GetPullRequestAsync(1, default);

        var ex = (await act.Should().ThrowAsync<GitHubStatusApiException>()).Which;
        ex.Category.Should().Be(GitHubStatusFailureCategory.RateLimited);
        ex.RateLimitResetUtc!.Value.ToUnixTimeSeconds().Should().Be(resetUnix);
    }

    [Fact]
    public async Task Forbidden_with_remaining_budget_stays_a_credentials_forbidden()
    {
        // A 403 without a rate-limit signal (budget remaining, no Retry-After) is a genuine authorization/
        // credentials rejection, not rate limiting.
        var handler = new StubHandler(_ => (
            HttpStatusCode.Forbidden,
            "{\"message\":\"Resource not accessible\"}",
            new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "4999" }));
        var client = NewClient(handler);

        var act = async () => await client.GetPullRequestAsync(1, default);

        var ex = (await act.Should().ThrowAsync<GitHubStatusApiException>()).Which;
        ex.Category.Should().Be(GitHubStatusFailureCategory.Forbidden);
    }

    [Fact]
    public async Task Timeout_maps_to_the_timeout_category()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        var client = NewClient(handler);

        var act = async () => await client.GetPullRequestAsync(1, default);

        var ex = (await act.Should().ThrowAsync<GitHubStatusApiException>()).Which;
        ex.Category.Should().Be(GitHubStatusFailureCategory.Timeout);
    }

    [Fact]
    public async Task Transient_500_is_retried_then_succeeds()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts < 2
                ? (HttpStatusCode.InternalServerError, "{}", null)
                : Ok("{\"state\":\"open\",\"merged\":false,\"head\":{\"sha\":\"x\"}}");
        });
        var client = NewClient(handler);

        var snapshot = await client.GetPullRequestAsync(1, default);

        snapshot.State.Should().Be("open");
        attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    private static GitHubRestPullRequestStatusClient NewClient(StubHandler handler)
        => new(new HttpClient(handler), Settings, new StubCredentialProvider(), NullLogger<GitHubRestPullRequestStatusClient>.Instance);

    private static (HttpStatusCode, string, Dictionary<string, string>?) Ok(string json)
        => (HttpStatusCode.OK, json, null);

    private sealed class StubCredentialProvider : IGitCredentialProvider
    {
        public Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(new GitHubCredential("test-token"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Json, Dictionary<string, string>? Headers)> _responder;

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string, Dictionary<string, string>?)> responder)
            => _responder = responder;

        public List<(string Method, string Path, string Query, string? Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri!.Query,
                request.Headers.Authorization?.ToString()));

            var (status, json, headers) = _responder(request);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };

            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                {
                    if (key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                    {
                        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(int.Parse(value)));
                    }
                    else
                    {
                        response.Headers.TryAddWithoutValidation(key, value);
                    }
                }
            }

            return Task.FromResult(response);
        }
    }
}
