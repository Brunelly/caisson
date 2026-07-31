using System.Net;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Ingestion.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubRestPullRequestClient"/> (story #172, Task #204) over a stubbed
/// <see cref="HttpMessageHandler"/>: the create flow uses the expected verbs/paths, never targets a
/// <c>/merges</c> or default-branch-update endpoint, sets a redactable bearer token, maps error responses to
/// a typed <see cref="GitHubApiException"/>, treats a 404 file as absent, and retries transient failures.
/// </summary>
public sealed class GitHubPullRequestClientTests
{
    private static readonly GitHubClientSettings Settings = new("https://api.github.test", "octo", "repo");

    [Fact]
    public async Task Create_flow_uses_expected_verbs_and_paths_and_never_a_merge_endpoint()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return (request.Method.Method, path) switch
            {
                ("GET", "/repos/octo/repo") => Ok("{\"default_branch\":\"main\"}"),
                ("GET", "/repos/octo/repo/branches/main") => Ok("{\"name\":\"main\",\"commit\":{\"sha\":\"base123\"}}"),
                ("POST", "/repos/octo/repo/git/refs") => Created("{\"ref\":\"refs/heads/f\",\"object\":{\"sha\":\"base123\"}}"),
                ("GET", "/repos/octo/repo/contents/desired-state/racks/rack-a.yaml") => (HttpStatusCode.NotFound, "{}"),
                ("PUT", "/repos/octo/repo/contents/desired-state/racks/rack-a.yaml") => Created("{\"commit\":{\"sha\":\"commit456\"}}"),
                ("POST", "/repos/octo/repo/pulls") => Created(
                    "{\"number\":7,\"html_url\":\"https://gh/pr/7\",\"head\":{\"ref\":\"f\"},\"base\":{\"ref\":\"main\"},\"state\":\"open\"}"),
                _ => (HttpStatusCode.InternalServerError, "{}"),
            };
        });
        var client = NewClient(handler);

        var repo = await client.GetRepositoryAsync(default);
        repo.DefaultBranch.Should().Be("main");

        var head = await client.GetBranchHeadAsync("main", default);
        head.CommitSha.Should().Be("base123");

        await client.CreateBranchAsync("caisson/rack-a/op-jdoe/x", "base123", default);

        var file = await client.GetFileMetadataAsync("caisson/rack-a/op-jdoe/x", "desired-state/racks/rack-a.yaml", default);
        file.Should().BeNull();

        var commit = await client.CommitFileAsync(
            "caisson/rack-a/op-jdoe/x", "desired-state/racks/rack-a.yaml", "yaml: true", "msg", null, default);
        commit.Sha.Should().Be("commit456");

        var pr = await client.OpenPullRequestAsync("title", "body", "caisson/rack-a/op-jdoe/x", "main", default);
        pr.Number.Should().Be(7);
        pr.HtmlUrl.Should().Be("https://gh/pr/7");

        // Structural safety boundary: no request ever targeted a merge or default-ref-update endpoint.
        handler.Requests.Should().NotContain(r => r.Path.Contains("/merges", StringComparison.OrdinalIgnoreCase));
        handler.Requests.Should().NotContain(r => r.Path.EndsWith("/git/refs/heads/main", StringComparison.OrdinalIgnoreCase));
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer test-token");
    }

    [Fact]
    public async Task GetFileMetadata_returns_null_on_404()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.NotFound, "{}"));
        var client = NewClient(handler);

        (await client.GetFileMetadataAsync("branch", "path.yaml", default)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Error_responses_map_to_a_typed_exception_with_the_status(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => (status, "{\"message\":\"secret-detail\"}"));
        var client = NewClient(handler);

        var act = async () => await client.OpenPullRequestAsync("t", "b", "head", "main", default);

        var ex = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        ex.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task Transient_500_is_retried_then_succeeds()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts < 2
                ? (HttpStatusCode.InternalServerError, "{}")
                : Ok("{\"default_branch\":\"main\"}");
        });
        var client = NewClient(handler);

        var repo = await client.GetRepositoryAsync(default);

        repo.DefaultBranch.Should().Be("main");
        attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_non_idempotent_post_5xx_is_not_retried()
    {
        // A 5xx on the create-ref/open-PR POST may reflect a partial success (the ref/PR was created); a blind
        // retry would then hit a 422 "already exists" and surface a spurious failure. So POST 5xx is surfaced
        // immediately (exactly one attempt), never retried.
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return (HttpStatusCode.InternalServerError, "{}");
        });
        var client = NewClient(handler);

        var act = async () => await client.OpenPullRequestAsync("t", "b", "head", "main", default);

        await act.Should().ThrowAsync<GitHubApiException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_post_429_is_still_retried_because_the_request_was_never_processed()
    {
        // A 429 means the request was rejected/rate-limited and never processed, so it is safe to retry even a
        // non-idempotent POST.
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts < 2
                ? (HttpStatusCode.TooManyRequests, "{}")
                : Created("{\"number\":7,\"html_url\":\"https://gh/pr/7\",\"head\":{\"ref\":\"f\"},\"base\":{\"ref\":\"main\"},\"state\":\"open\"}");
        });
        var client = NewClient(handler);

        var pr = await client.OpenPullRequestAsync("t", "b", "head", "main", default);

        pr.Number.Should().Be(7);
        attempts.Should().Be(2);
    }

    private static GitHubRestPullRequestClient NewClient(StubHandler handler)
        => new(new HttpClient(handler), Settings, new StubCredentialProvider(), NullLogger<GitHubRestPullRequestClient>.Instance);

    private static (HttpStatusCode, string) Ok(string json) => (HttpStatusCode.OK, json);

    private static (HttpStatusCode, string) Created(string json) => (HttpStatusCode.Created, json);

    private sealed class StubCredentialProvider : IGitCredentialProvider
    {
        public Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(new GitHubCredential("test-token"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> _responder;

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

        public List<(string Method, string Path, string? Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            var (status, json) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
