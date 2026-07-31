using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the desired-state PR idempotency link (story #172, Task #206): the filtered
/// partial-unique index (one Open link per rack+fingerprint, a Closed/Merged link does not block a fresh
/// one), the rack FK Restrict, and the insert-or-get store's concurrent-conflict resolution.
/// </summary>
public sealed class GitPullRequestLinkPersistenceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public GitPullRequestLinkPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Enforces_one_open_link_per_rack_and_fingerprint()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();

        await using var context = _fixture.CreateContext();
        context.GitPullRequestLinks.Add(Link(rackId, fingerprint, "caisson/a"));
        await context.SaveChangesAsync();

        context.GitPullRequestLinks.Add(Link(rackId, fingerprint, "caisson/b"));
        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_closed_link_does_not_block_a_new_open_link_for_the_same_fingerprint()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();

        await using (var context = _fixture.CreateContext())
        {
            var first = Link(rackId, fingerprint, "caisson/a");
            first.MarkPublished(1, "https://gh/pr/1", "commitsha1", DateTime.UtcNow);
            first.UpdateStatus(GitPullRequestStatus.Closed, DateTime.UtcNow);
            context.GitPullRequestLinks.Add(first);
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        verify.GitPullRequestLinks.Add(Link(rackId, fingerprint, "caisson/b"));
        var act = async () => await verify.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Rack_foreign_key_is_restrict()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using (var context = _fixture.CreateContext())
        {
            context.GitPullRequestLinks.Add(Link(rackId, Hex(), "caisson/a"));
            await context.SaveChangesAsync();
        }

        await using var delete = _fixture.CreateContext();
        var rack = await delete.Racks.SingleAsync(r => r.Id == rackId);
        delete.Racks.Remove(rack);
        var act = async () => await delete.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task InsertOrGetExisting_returns_the_winner_on_a_conflict()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();

        await using (var seed = _fixture.CreateContext())
        {
            await new GitPullRequestLinkStore(seed).InsertOrGetExistingAsync(Link(rackId, fingerprint, "caisson/winner"));
        }

        await using var context = _fixture.CreateContext();
        var reservation = await new GitPullRequestLinkStore(context)
            .InsertOrGetExistingAsync(Link(rackId, fingerprint, "caisson/loser"));

        reservation.Inserted.Should().BeFalse();
        reservation.Link.BranchName.Should().Be("caisson/winner");
    }

    [Fact]
    public async Task FindOpenByFingerprint_ignores_closed_links()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();

        await using (var seed = _fixture.CreateContext())
        {
            var link = Link(rackId, fingerprint, "caisson/a");
            link.UpdateStatus(GitPullRequestStatus.Merged, DateTime.UtcNow);
            seed.GitPullRequestLinks.Add(link);
            await seed.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext();
        var found = await new GitPullRequestLinkStore(context).FindOpenByFingerprintAsync(rackId, fingerprint);

        found.Should().BeNull();
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private static GitPullRequestLink Link(Guid rackId, string fingerprint, string branch)
        => new(Guid.NewGuid(), rackId, "octo", "repo", branch, fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());

    private static string Hex() => (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
}
