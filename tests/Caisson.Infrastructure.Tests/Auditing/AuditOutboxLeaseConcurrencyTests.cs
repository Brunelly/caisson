using Caisson.Api.Options;
using Caisson.Domain.Auditing;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 1 outbox dispatcher's lease/retry/poison contract (story #308, ADR 0064) against real
/// PostgreSQL — <c>FOR UPDATE SKIP LOCKED</c> concurrency, lease expiry reclaim, and the poison path never
/// leaking a raw exception message.
/// </summary>
public sealed class AuditOutboxLeaseConcurrencyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditOutboxLeaseConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_concurrent_dispatchers_never_double_claim_the_same_due_row()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(await SeedPendingMessageAsync());
        }

        var now = DateTime.UtcNow;
        var lease = now.AddSeconds(120);

        async Task<IReadOnlyList<Guid>> ClaimAsync()
        {
            await using var context = _fixture.CreateContext();
            return await AuditOutboxQueries.ClaimDueAsync(context, now, lease, Guid.NewGuid().ToString("N"), batchSize: 100, default);
        }

        var replicaA = ClaimAsync();
        var replicaB = ClaimAsync();
        var results = await Task.WhenAll(replicaA, replicaB);

        var claimed = results[0].Concat(results[1]).ToList();
        claimed.Should().OnlyHaveUniqueItems();
        claimed.Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimable_but_a_fresh_lease_is_not()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();
        var id = await SeedPendingMessageAsync();

        var now = DateTime.UtcNow;

        await using (var context = _fixture.CreateContext())
        {
            var claimed = await AuditOutboxQueries.ClaimDueAsync(context, now, now.AddSeconds(60), "instance-a", 10, default);
            claimed.Should().ContainSingle().Which.Should().Be(id);
        }

        // Lease is still fresh (60s ahead) — a second dispatcher must not reclaim it.
        await using (var context = _fixture.CreateContext())
        {
            var claimed = await AuditOutboxQueries.ClaimDueAsync(context, now, now.AddSeconds(60), "instance-b", 10, default);
            claimed.Should().BeEmpty();
        }

        // Once the lease horizon has passed, the row becomes claimable again (crashed dispatcher recovery).
        var afterLease = now.AddSeconds(120);
        await using (var context = _fixture.CreateContext())
        {
            var claimed = await AuditOutboxQueries.ClaimDueAsync(context, afterLease, afterLease.AddSeconds(60), "instance-c", 10, default);
            claimed.Should().ContainSingle().Which.Should().Be(id);
        }
    }

    [Fact]
    public async Task A_permanently_failing_row_is_poisoned_after_max_attempts_with_no_sensitive_text_in_the_failure_code()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();

        // A rack_id that references no Rack row: topology_audit_event's FK on rack_id makes every dispatch
        // attempt fail with a foreign-key violation — a stand-in for "permanently failing" dispatch.
        var nonExistentRackId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext())
        {
            seed.AuditOutboxMessages.Add(new AuditOutboxMessage(
                id, DateTime.UtcNow, ActorType.User, "actor-1", "network-intent.saved", "rack",
                targetId: nonExistentRackId.ToString(), correlationId: Guid.NewGuid(), result: "success",
                rackId: nonExistentRackId, snapshotId: null, detailsJson: null, availableAtUtc: DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = new AuditDurabilityOptions
        {
            OutboxMaxAttempts = 3,
            OutboxRetryBaseDelaySeconds = 1,
            OutboxRetryMaxDelaySeconds = 5,
            OutboxLeaseSeconds = 1,
        };
        var dispatcher = AuditOutboxDispatcherTestFactory.Create(_fixture, time, options);

        for (var attempt = 0; attempt < options.OutboxMaxAttempts; attempt++)
        {
            await dispatcher.TickAsync(default);
            time.Advance(TimeSpan.FromSeconds(options.OutboxRetryMaxDelaySeconds + 1));
        }

        await using var verify = _fixture.CreateContext();
        var message = await verify.AuditOutboxMessages.SingleAsync(m => m.Id == id);
        message.Status.Should().Be(AuditOutboxStatus.Poisoned);
        // Stable, sanitized code only — never the raw exception message (which would embed the
        // constraint/table name and, in other failure modes, could embed connection details).
        message.FailureCode.Should().Be("ForeignKeyViolation");
        message.FailureCode!.Length.Should().BeLessOrEqualTo(AuditOutboxMessage.MaxFailureCodeLength);

        // Never dispatched, and the full payload is retained (never deleted) for operator triage.
        (await verify.AuditEvents.CountAsync(e => e.Id == id)).Should().Be(0);
    }

    /// <summary>The dispatcher's claim query is global (not scoped to specific ids); clear leftover rows from other tests in this class first.</summary>
    private async Task ResetAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM audit_outbox;");
    }

    private async Task<Guid> SeedPendingMessageAsync()
    {
        var id = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.AuditOutboxMessages.Add(new AuditOutboxMessage(
            id, DateTime.UtcNow, ActorType.System, "system", "test.action", "test-target", targetId: null,
            correlationId: Guid.NewGuid(), result: "success", rackId: null, snapshotId: null,
            detailsJson: null, availableAtUtc: DateTime.UtcNow));
        await context.SaveChangesAsync();
        return id;
    }
}
