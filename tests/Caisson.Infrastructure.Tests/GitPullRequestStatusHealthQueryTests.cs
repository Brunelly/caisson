using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed test for <see cref="GitPullRequestStatusQueries.HealthSnapshotAsync"/> (story #173,
/// Task #218): a never-polled first-sighted record (zero failures but no observed head SHA) must NOT count as
/// a recent successful poll — even when it is the newest record — so it cannot suppress the poller's Degraded
/// health signal. Before the fix the snapshot selected the newest zero-failure row regardless of whether it had
/// ever been observed.
/// </summary>
public sealed class GitPullRequestStatusHealthQueryTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTime ObservedAt = new(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerFirstSightedAt = new(2026, 7, 31, 11, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public GitPullRequestStatusHealthQueryTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Newest_never_polled_record_does_not_masquerade_as_the_recent_successful_poll()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        // An older record that WAS successfully observed (has a head SHA), and a NEWER first-sighted record that
        // has never been polled (null head SHA, zero failures) — the "recent success" must be the observed one.
        await SeedRecordAsync(rackId, observedAt: ObservedAt);
        await SeedRecordAsync(rackId, observedAt: null, createdAt: NewerFirstSightedAt);

        await using var ctx = _fixture.CreateContext();
        var health = await GitPullRequestStatusQueries.HealthSnapshotAsync(ctx, default);

        health.TotalRecords.Should().Be(2);
        health.LastSuccessfulPollAtUtc.Should().Be(ObservedAt,
            "only the observed record is a genuine successful poll; the newer never-polled record must not count");
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Health Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedRecordAsync(Guid rackId, DateTime? observedAt, DateTime? createdAt = null)
    {
        await using var context = _fixture.CreateContext();
        var created = createdAt ?? new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
        var linkId = Guid.NewGuid();
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/" + Guid.NewGuid().ToString("N")[..8],
            (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64], "tester", created,
            Guid.NewGuid().ToString());
        link.MarkPublished(7, "https://gh/pr/7", "commitshax", created);

        // First-sighted (never polled): the constructor leaves HeadSha null and ConsecutivePollFailures 0.
        var record = new GitPullRequestStatusRecord(
            Guid.NewGuid(), linkId, rackId, "octo", "repo", 7, "https://gh/pr/7", created);
        if (observedAt is { } at)
        {
            record.ApplyObservation(
                GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", at);
        }

        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(record);
        await context.SaveChangesAsync();
    }
}
