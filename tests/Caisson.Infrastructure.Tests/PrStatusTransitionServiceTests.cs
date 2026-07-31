using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Ingestion.Git.GitHub;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for <see cref="PrStatusTransitionService"/> (story #173, Tasks #212/#214): each
/// meaningful transition writes an append-only audit row (state and/or checks) in the same transaction as the
/// status upsert, with actor=system/correlationId/previous+new and no secrets, and publishes exactly one
/// fail-open status-changed event.
/// </summary>
public sealed class PrStatusTransitionServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PrStatusTransitionServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_state_transition_writes_one_status_changed_audit_and_publishes_one_event()
    {
        await _fixture.MigrateAsync();
        var (rackId, linkId) = await SeedAsync();
        var publisher = new RecordingTopologyEventPublisher();
        var correlationId = Guid.NewGuid();

        await using (var ctx = _fixture.CreateContext())
        {
            var record = await ctx.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
            var previous = new PrStatusTransitionSnapshot(record.State, record.ChecksConclusion);
            record.ApplyObservation(GitPullRequestStatus.Merged, "sha1", record.ChecksConclusion, null, "{}", DateTime.UtcNow);

            var service = NewService(publisher);
            await service.OnStatusChangedAsync(ctx, record, previous, correlationId, default);
        }

        await using var verify = _fixture.CreateContext();
        var audits = await verify.AuditEvents.Where(a => a.CorrelationId == correlationId).ToListAsync();
        audits.Should().ContainSingle();
        var audit = audits[0];
        audit.Action.Should().Be(GitPrStatusAuditActions.StatusChanged);
        audit.ActorType.Should().Be(Caisson.Domain.Enums.ActorType.System);
        audit.ActorId.Should().Be("system");
        audit.RackId.Should().Be(rackId);
        audit.DetailsJson.Should().Contain("Merged");
        audit.DetailsJson.Should().NotContain("token");

        publisher.GitPullRequestStatuses.Should().ContainSingle();
        publisher.GitPullRequestStatuses[0].State.Should().Be("Merged");
        publisher.GitPullRequestStatuses[0].RackId.Should().Be(rackId);
    }

    [Fact]
    public async Task A_combined_state_and_checks_transition_writes_two_audits()
    {
        await _fixture.MigrateAsync();
        var (_, linkId) = await SeedAsync();
        var publisher = new RecordingTopologyEventPublisher();
        var correlationId = Guid.NewGuid();

        await using (var ctx = _fixture.CreateContext())
        {
            var record = await ctx.GitPullRequestStatuses.SingleAsync(x => x.PullRequestLinkId == linkId);
            var previous = new PrStatusTransitionSnapshot(record.State, record.ChecksConclusion);
            // From Open/Unknown → Merged/Success: both state and checks change.
            record.ApplyObservation(GitPullRequestStatus.Merged, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", DateTime.UtcNow);

            await NewService(publisher).OnStatusChangedAsync(ctx, record, previous, correlationId, default);
        }

        await using var verify = _fixture.CreateContext();
        var audits = await verify.AuditEvents.Where(a => a.CorrelationId == correlationId).ToListAsync();
        audits.Should().HaveCount(2);
        audits.Select(a => a.Action).Should().BeEquivalentTo(new[]
        {
            GitPrStatusAuditActions.StatusChanged, GitPrStatusAuditActions.ChecksChanged,
        });
    }

    private PrStatusTransitionService NewService(RecordingTopologyEventPublisher publisher)
        => new(publisher, new FakeSequencer(), TimeProvider.System, NullLogger<PrStatusTransitionService>.Instance);

    private async Task<(Guid RackId, Guid LinkId)> SeedAsync()
    {
        // Audit rows are append-only (DB trigger blocks DELETE); tests isolate by a unique correlationId
        // instead. Only the mutable link/status rows are cleared.
        await using (var reset = _fixture.CreateContext())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DELETE FROM git_pull_request_status; DELETE FROM git_pull_request_link;");
        }

        var rackId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        var fingerprint = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/a", fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(7, "https://gh/pr/7", "commitsha7", DateTime.UtcNow);
        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(
            new GitPullRequestStatusRecord(Guid.NewGuid(), linkId, rackId, "octo", "repo", 7, "https://gh/pr/7", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return (rackId, linkId);
    }

    private sealed class FakeSequencer : ITopologyEventSequencer
    {
        private long _seq;

        public ValueTask<long> NextAsync(string stream, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Interlocked.Increment(ref _seq));
    }
}
