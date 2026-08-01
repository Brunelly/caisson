using Caisson.Api.Options;
using Caisson.Domain.Auditing;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves story #308 AC2's dispatch-side idempotency against real PostgreSQL: dispatching an outbox row
/// (or a crash-and-redispatch of the same row) produces exactly ONE <c>topology_audit_event</c> row, whose
/// id equals the outbox row's id.
/// </summary>
public sealed class AuditOutboxDispatchIdempotencyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditOutboxDispatchIdempotencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProjectToAuditEventAsync_dispatches_a_claimed_row_to_exactly_one_audit_event_with_the_outbox_id()
    {
        await _fixture.MigrateAsync();
        var id = await SeedPendingMessageAsync("discovery.job.succeeded");

        await using var context = _fixture.CreateContext();
        await AuditOutboxQueries.ProjectToAuditEventAsync(context, id, default);

        var audits = await context.AuditEvents.Where(e => e.Id == id).ToListAsync();
        audits.Should().ContainSingle();
        audits[0].Action.Should().Be("discovery.job.succeeded");
    }

    [Fact]
    public async Task Redispatching_the_same_outbox_id_creates_no_second_audit_row()
    {
        await _fixture.MigrateAsync();
        var id = await SeedPendingMessageAsync("discovery.job.succeeded");

        await using var context = _fixture.CreateContext();
        await AuditOutboxQueries.ProjectToAuditEventAsync(context, id, default);
        await AuditOutboxQueries.ProjectToAuditEventAsync(context, id, default);
        await AuditOutboxQueries.ProjectToAuditEventAsync(context, id, default);

        (await context.AuditEvents.CountAsync(e => e.Id == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_crash_between_claim_and_commit_leaves_the_row_pending_and_it_is_redispatched_without_duplication()
    {
        await _fixture.MigrateAsync();
        var id = await SeedPendingMessageAsync("drift.apply.job.completed");

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = new AuditDurabilityOptions { OutboxLeaseSeconds = 5 };
        var dispatcher = AuditOutboxDispatcherTestFactory.Create(_fixture, time, options);

        // First tick dispatches and marks the row Dispatched in one transaction.
        await dispatcher.TickAsync(default);

        await using (var verify = _fixture.CreateContext())
        {
            (await verify.AuditEvents.CountAsync(e => e.Id == id)).Should().Be(1);
            var message = await verify.AuditOutboxMessages.SingleAsync(m => m.Id == id);
            message.Status.Should().Be(AuditOutboxStatus.Dispatched);
        }

        // Simulate a crash that dispatched the audit event but crashed before the SAME transaction's
        // status flip committed: force the row back to Pending (the transactional guarantee means this
        // combination cannot occur in production — the point here is that IF a lease-expired row were
        // re-claimed, redispatch must still not duplicate the audit event).
        await using (var reset = _fixture.CreateContext())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "UPDATE audit_outbox SET status = 'Pending', lease_until_utc = NULL, dispatched_at_utc = NULL WHERE id = {0}",
                id);
        }

        time.Advance(TimeSpan.FromSeconds(10));
        await dispatcher.TickAsync(default);

        await using var final = _fixture.CreateContext();
        (await final.AuditEvents.CountAsync(e => e.Id == id)).Should().Be(1);
        (await final.AuditOutboxMessages.SingleAsync(m => m.Id == id)).Status.Should().Be(AuditOutboxStatus.Dispatched);
    }

    private async Task<Guid> SeedPendingMessageAsync(string action)
    {
        var id = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.AuditOutboxMessages.Add(new AuditOutboxMessage(
            id, DateTime.UtcNow, ActorType.System, "system", action, "test-target", targetId: null,
            correlationId: Guid.NewGuid(), result: "success", rackId: null, snapshotId: null,
            detailsJson: null, availableAtUtc: DateTime.UtcNow));
        await context.SaveChangesAsync();
        return id;
    }
}
