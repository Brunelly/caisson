using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the PR status poller's DB lease (story #173, Task #211b): the
/// <c>UPDATE ... FOR UPDATE SKIP LOCKED</c> claim never lets two concurrent replicas double-claim the same
/// PR, first-sighting upserts a status row per published link, and only due candidates are claimed.
/// </summary>
public sealed class GitPullRequestStatusLeaseTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public GitPullRequestStatusLeaseTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_first_sights_a_status_row_per_published_open_link()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        await SeedPublishedLinkAsync(rackId);
        await SeedPublishedLinkAsync(rackId);

        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.CreateContext())
        {
            var inserted = await GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync(ctx, now, default);
            inserted.Should().Be(2);
        }

        // Idempotent: a second upsert inserts nothing (ON CONFLICT DO NOTHING).
        await using (var ctx = _fixture.CreateContext())
        {
            var inserted = await GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync(ctx, now, default);
            inserted.Should().Be(0);
        }
    }

    [Fact]
    public async Task Two_concurrent_replicas_never_double_claim_the_same_due_pr()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        for (var i = 0; i < 8; i++)
        {
            await SeedPublishedLinkAsync(rackId);
        }

        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.CreateContext())
        {
            await GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync(ctx, now, default);
        }

        var lease = now.AddSeconds(120);

        async Task<IReadOnlyList<Guid>> ClaimAsync()
        {
            await using var ctx = _fixture.CreateContext();
            return await GitPullRequestStatusQueries.ClaimDueAsync(ctx, now, lease, batchSize: 100, default);
        }

        var replicaA = ClaimAsync();
        var replicaB = ClaimAsync();
        var results = await Task.WhenAll(replicaA, replicaB);

        var all = results[0].Concat(results[1]).ToList();
        // Each PR claimed exactly once (no overlap), and all 8 due PRs claimed across the two replicas.
        all.Should().OnlyHaveUniqueItems();
        all.Should().HaveCount(8);
    }

    [Fact]
    public async Task A_claim_advances_next_poll_so_the_row_is_not_reclaimed_within_the_lease()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        await SeedPublishedLinkAsync(rackId);

        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.CreateContext())
        {
            await GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync(ctx, now, default);
        }

        var lease = now.AddSeconds(120);
        await using (var ctx = _fixture.CreateContext())
        {
            var first = await GitPullRequestStatusQueries.ClaimDueAsync(ctx, now, lease, 100, default);
            first.Should().ContainSingle();
        }

        // A second tick at the same instant sees the leased row as not-due and claims nothing.
        await using (var ctx = _fixture.CreateContext())
        {
            var second = await GitPullRequestStatusQueries.ClaimDueAsync(ctx, now, lease, 100, default);
            second.Should().BeEmpty();
        }

        // After the lease expires the row is due again.
        await using (var ctx = _fixture.CreateContext())
        {
            var third = await GitPullRequestStatusQueries.ClaimDueAsync(ctx, now.AddSeconds(200), now.AddSeconds(320), 100, default);
            third.Should().ContainSingle();
        }
    }

    // The claim query is global (polls all due PRs), so isolate the shared class DB between tests.
    private async Task ResetAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM git_pull_request_status; DELETE FROM git_pull_request_link;");
    }

    private async Task<Guid> SeedRackAsync()
    {
        await ResetAsync();
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedPublishedLinkAsync(Guid rackId)
    {
        await using var context = _fixture.CreateContext();
        var fingerprint = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
        var link = new GitPullRequestLink(
            Guid.NewGuid(), rackId, "octo", "repo", "caisson/" + Guid.NewGuid().ToString("N")[..8],
            fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(Random.Shared.Next(1, 100000), "https://gh/pr/x", "commitshax", DateTime.UtcNow);
        context.GitPullRequestLinks.Add(link);
        await context.SaveChangesAsync();
    }
}
