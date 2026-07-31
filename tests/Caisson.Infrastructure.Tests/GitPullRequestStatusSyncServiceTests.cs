using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for <see cref="GitPullRequestStatusSyncService"/> (story #173, Task #211b): the
/// exactly-two-GitHub-calls budget, the Merged/Closed link dual-write, transition hand-off only on a
/// meaningful change, and the 401/403 (no audit/no event, backoff) + 429 (rate-limit timing) failure paths.
/// </summary>
public sealed class GitPullRequestStatusSyncServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public GitPullRequestStatusSyncServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task First_poll_of_an_open_pr_makes_two_calls_and_records_a_meaningful_transition()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId, number) = await SeedLinkAsync();

        var github = new FakeGitHub
        {
            Pr = new GitHubPullRequestSnapshot("open", false, "sha1"),
            CheckRuns = new GitHubCheckRunsResult(1, new[]
            {
                new GitHubCheckRun(1, "build", "completed", "success", null, null, null),
            }),
        };
        var transitions = new FakeTransitions();

        await using (var ctx = _fixture.CreateContext())
        {
            var service = NewService(ctx, github, transitions);
            var polled = await service.SyncDueAsync(Guid.NewGuid(), default);
            polled.Should().Be(1);
        }

        github.PullRequestCalls.Should().Be(1);
        github.CheckRunCalls.Should().Be(1);
        github.CheckRunShas.Should().ContainSingle().Which.Should().Be("sha1");
        transitions.Calls.Should().ContainSingle();

        await using var verify = _fixture.CreateContext();
        var record = await verify.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
        record.State.Should().Be(GitPullRequestStatus.Open);
        record.ChecksConclusion.Should().Be(GitPullRequestChecksConclusion.Success);
        record.HeadSha.Should().Be("sha1");
    }

    [Fact]
    public async Task A_repeated_unchanged_poll_records_no_transition()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId, number) = await SeedLinkAsync();
        var github = new FakeGitHub
        {
            Pr = new GitHubPullRequestSnapshot("open", false, "sha1"),
            CheckRuns = new GitHubCheckRunsResult(1, new[]
            {
                new GitHubCheckRun(1, "build", "completed", "success", null, null, null),
            }),
        };
        var transitions = new FakeTransitions();

        await Poll(github, transitions);        // first poll: transition
        MakeDue(linkId);
        await Poll(github, transitions);        // second poll: unchanged

        transitions.Calls.Should().ContainSingle("only the first, changing, poll is a transition");
    }

    [Fact]
    public async Task Merged_pr_flips_the_link_status_in_the_same_transaction()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId, number) = await SeedLinkAsync();
        var github = new FakeGitHub
        {
            Pr = new GitHubPullRequestSnapshot("closed", true, "sha1"),
            CheckRuns = new GitHubCheckRunsResult(1, new[]
            {
                new GitHubCheckRun(1, "build", "completed", "success", null, null, null),
            }),
        };
        var transitions = new FakeTransitions();

        await Poll(github, transitions);

        await using var verify = _fixture.CreateContext();
        var record = await verify.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
        record.State.Should().Be(GitPullRequestStatus.Merged);
        var link = await verify.GitPullRequestLinks.SingleAsync(x => x.Id == linkId);
        link.Status.Should().Be(GitPullRequestStatus.Merged);
    }

    [Fact]
    public async Task Credentials_rejected_records_a_sanitized_failure_with_backoff_and_no_transition()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId, number) = await SeedLinkAsync();
        var github = new FakeGitHub
        {
            PrException = new GitHubStatusApiException(GitHubStatusFailureCategory.Unauthorized, "GET", "/pulls/1", 401),
        };
        var transitions = new FakeTransitions();

        var now = new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
        await Poll(github, transitions, now);

        transitions.Calls.Should().BeEmpty();
        github.CheckRunCalls.Should().Be(0, "the checks call is skipped once the PR call fails");

        await using var verify = _fixture.CreateContext();
        var record = await verify.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
        record.LastPollFailureReason.Should().Be(GitPrPollFailureReasons.CredentialsRejected);
        record.ConsecutivePollFailures.Should().Be(1);
        record.NextPollAfterUtc.Should().BeAfter(now);
    }

    [Fact]
    public async Task Rate_limited_poll_respects_retry_after_timing()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId, number) = await SeedLinkAsync();
        var github = new FakeGitHub
        {
            PrException = new GitHubStatusApiException(
                GitHubStatusFailureCategory.RateLimited, "GET", "/pulls/1", 429, TimeSpan.FromSeconds(300)),
        };
        var transitions = new FakeTransitions();

        var now = new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
        await Poll(github, transitions, now);

        await using var verify = _fixture.CreateContext();
        var record = await verify.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
        record.LastPollFailureReason.Should().Be(GitPrPollFailureReasons.RateLimited);
        record.NextPollAfterUtc.Should().Be(now.AddSeconds(300));
    }

    private async Task Poll(FakeGitHub github, FakeTransitions transitions, DateTime? now = null)
    {
        await using var ctx = _fixture.CreateContext();
        var service = NewService(ctx, github, transitions, now);
        await service.SyncDueAsync(Guid.NewGuid(), default);
    }

    private void MakeDue(Guid linkId)
    {
        using var ctx = _fixture.CreateContext();
        var record = ctx.GitPullRequestStatuses.Single(x => x.PullRequestLinkId == linkId);
        typeof(GitPullRequestStatusRecord).GetProperty(nameof(GitPullRequestStatusRecord.NextPollAfterUtc))!
            .SetValue(record, DateTime.UtcNow.AddMinutes(-1));
        ctx.SaveChanges();
    }

    private static GitPullRequestStatusSyncService NewService(
        CaissonDbContext ctx, FakeGitHub github, FakeTransitions transitions, DateTime? now = null)
    {
        var time = now is null ? TimeProvider.System : new FixedTimeProvider(now.Value);
        return new GitPullRequestStatusSyncService(
            ctx,
            github,
            transitions,
            Microsoft.Extensions.Options.Options.Create(new GitPullRequestStatusOptions { PollIntervalSeconds = 60, BatchSize = 50, LeaseSeconds = 120 }),
            time,
            new Caisson.Ingestion.Observability.GitPullRequestStatusMetrics(),
            NullLogger<GitPullRequestStatusSyncService>.Instance);
    }

    private async Task<(Guid RackId, Guid LinkId, int Number)> SeedLinkAsync()
    {
        // The sync claim is global, so isolate the shared class DB between tests.
        await using (var reset = _fixture.CreateContext())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DELETE FROM git_pull_request_status; DELETE FROM git_pull_request_link;");
        }

        var rackId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var number = Random.Shared.Next(1, 100000);
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        var fingerprint = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/a", fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(number, "https://gh/pr/" + number, "commitsha", DateTime.UtcNow);
        context.GitPullRequestLinks.Add(link);
        await context.SaveChangesAsync();
        return (rackId, linkId, number);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now) => _now = new DateTimeOffset(now, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeGitHub : IGitHubPullRequestStatusClient
    {
        public GitHubPullRequestSnapshot Pr { get; set; } = new("open", false, "sha1");

        public GitHubCheckRunsResult CheckRuns { get; set; } = new(0, Array.Empty<GitHubCheckRun>());

        public GitHubStatusApiException? PrException { get; set; }

        public GitHubStatusApiException? CheckException { get; set; }

        public int PullRequestCalls { get; private set; }

        public int CheckRunCalls { get; private set; }

        public List<string> CheckRunShas { get; } = new();

        public Task<GitHubPullRequestSnapshot> GetPullRequestAsync(int number, CancellationToken cancellationToken)
        {
            PullRequestCalls++;
            if (PrException is not null)
            {
                throw PrException;
            }

            return Task.FromResult(Pr);
        }

        public Task<GitHubCheckRunsResult> GetCheckRunsForRefAsync(string headSha, CancellationToken cancellationToken)
        {
            CheckRunCalls++;
            CheckRunShas.Add(headSha);
            if (CheckException is not null)
            {
                throw CheckException;
            }

            return Task.FromResult(CheckRuns);
        }
    }

    private sealed class FakeTransitions : IPrStatusTransitionService
    {
        public List<PrStatusTransitionSnapshot> Calls { get; } = new();

        public async Task OnStatusChangedAsync(
            CaissonDbContext context,
            GitPullRequestStatusRecord record,
            PrStatusTransitionSnapshot previous,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(previous);
            // The real service commits the whole unit of work here; the fake just persists the tracked changes.
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
