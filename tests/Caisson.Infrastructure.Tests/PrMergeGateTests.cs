using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Orchestration.Git;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the core merged-apply gate logic (story #173, Task #213): exact-candidate
/// matching (an unrelated merged PR does NOT unlock a different candidate), fail-closed on missing/unmerged
/// status, and allow only on a persisted Merged status for the exact fingerprint.
/// </summary>
public sealed class PrMergeGateTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PrMergeGateTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task No_link_for_the_fingerprint_is_no_pr_linked()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using var ctx = _fixture.CreateContext();
        var result = await new PrMergeGate(ctx).EvaluateAsync(rackId, Hex(), default);

        result.Reason.Should().Be(PrMergeGateReason.NoPrLinked);
    }

    [Fact]
    public async Task Open_pr_for_the_fingerprint_is_pr_not_merged()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();
        await SeedLinkWithStatusAsync(rackId, fingerprint, GitPullRequestStatus.Open);

        await using var ctx = _fixture.CreateContext();
        var result = await new PrMergeGate(ctx).EvaluateAsync(rackId, fingerprint, default);

        result.Reason.Should().Be(PrMergeGateReason.PrNotMerged);
        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Merged_pr_for_the_exact_fingerprint_is_allowed()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var fingerprint = Hex();
        await SeedLinkWithStatusAsync(rackId, fingerprint, GitPullRequestStatus.Merged);

        await using var ctx = _fixture.CreateContext();
        var result = await new PrMergeGate(ctx).EvaluateAsync(rackId, fingerprint, default);

        result.Reason.Should().Be(PrMergeGateReason.Allowed);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task An_unrelated_merged_pr_does_not_unlock_a_different_candidate()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var mergedFingerprint = Hex();
        var candidateFingerprint = Hex();
        await SeedLinkWithStatusAsync(rackId, mergedFingerprint, GitPullRequestStatus.Merged);

        await using var ctx = _fixture.CreateContext();
        // The candidate we are trying to apply has a DIFFERENT fingerprint — the unrelated merged PR must not
        // unlock it.
        var result = await new PrMergeGate(ctx).EvaluateAsync(rackId, candidateFingerprint, default);

        result.Reason.Should().Be(PrMergeGateReason.NoPrLinked);
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Gate Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedLinkWithStatusAsync(Guid rackId, string fingerprint, GitPullRequestStatus state)
    {
        await using var context = _fixture.CreateContext();
        var linkId = Guid.NewGuid();
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/" + Guid.NewGuid().ToString("N")[..8],
            fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(Random.Shared.Next(1, 100000), "https://gh/pr/x", "commitshax", DateTime.UtcNow);
        if (state != GitPullRequestStatus.Open)
        {
            link.UpdateStatus(state, DateTime.UtcNow);
        }

        var record = new GitPullRequestStatusRecord(
            Guid.NewGuid(), linkId, rackId, "octo", "repo", 1, "https://gh/pr/x", DateTime.UtcNow);
        record.ApplyObservation(state, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", DateTime.UtcNow);

        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(record);
        await context.SaveChangesAsync();
    }

    private static string Hex() => (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
}
