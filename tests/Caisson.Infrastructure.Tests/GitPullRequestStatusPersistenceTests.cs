using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the PR status projection (story #173, Task #210): the jsonb/enum-as-string/xmin
/// round-trip, the 1:1 unique index on <c>pull_request_link_id</c>, and the link FK Restrict — mirroring
/// <see cref="GitPullRequestLinkPersistenceTests"/>.
/// </summary>
public sealed class GitPullRequestStatusPersistenceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public GitPullRequestStatusPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Round_trips_jsonb_enums_and_xmin()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId) = await SeedLinkAsync();

        Guid recordId;
        await using (var context = _fixture.CreateContext())
        {
            var record = NewRecord(rackId, linkId);
            record.ApplyObservation(
                GitPullRequestStatus.Merged,
                "0123abcd",
                GitPullRequestChecksConclusion.Success,
                0,
                "{\"checks\":[{\"name\":\"build\",\"conclusion\":\"success\"}]}",
                DateTime.UtcNow);
            context.GitPullRequestStatuses.Add(record);
            await context.SaveChangesAsync();
            recordId = record.Id;
        }

        await using var verify = _fixture.CreateContext();
        var loaded = await verify.GitPullRequestStatuses.SingleAsync(x => x.Id == recordId);

        loaded.State.Should().Be(GitPullRequestStatus.Merged);
        loaded.ChecksConclusion.Should().Be(GitPullRequestChecksConclusion.Success);
        loaded.HeadSha.Should().Be("0123abcd");
        loaded.FailingChecksCount.Should().Be(0);
        loaded.ChecksSummary.Should().Contain("build");
        loaded.PullRequestLinkId.Should().Be(linkId);
        loaded.RackId.Should().Be(rackId);
    }

    [Fact]
    public async Task Enforces_one_status_record_per_link()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId) = await SeedLinkAsync();

        await using var context = _fixture.CreateContext();
        context.GitPullRequestStatuses.Add(NewRecord(rackId, linkId));
        await context.SaveChangesAsync();

        context.GitPullRequestStatuses.Add(NewRecord(rackId, linkId));
        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Link_foreign_key_is_restrict()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId) = await SeedLinkAsync();

        await using (var context = _fixture.CreateContext())
        {
            context.GitPullRequestStatuses.Add(NewRecord(rackId, linkId));
            await context.SaveChangesAsync();
        }

        await using var delete = _fixture.CreateContext();
        var link = await delete.GitPullRequestLinks.SingleAsync(x => x.Id == linkId);
        delete.GitPullRequestLinks.Remove(link);
        var act = async () => await delete.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static GitPullRequestStatusRecord NewRecord(Guid rackId, Guid linkId)
        => new(Guid.NewGuid(), linkId, rackId, "octo", "repo", 7, "https://gh/pr/7", DateTime.UtcNow);

    private async Task<(Guid RackId, Guid LinkId)> SeedLinkAsync()
    {
        var rackId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        var fingerprint = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/a", fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(7, "https://gh/pr/7", "commitsha7", DateTime.UtcNow);
        context.GitPullRequestLinks.Add(link);
        await context.SaveChangesAsync();
        return (rackId, linkId);
    }
}
