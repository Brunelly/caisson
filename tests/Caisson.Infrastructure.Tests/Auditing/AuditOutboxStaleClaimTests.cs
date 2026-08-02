using Caisson.Api.Options;
using Caisson.Domain.Auditing;
using Caisson.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 1 dispatcher never acts on a row it no longer owns (story #308, ADR 0064).
/// <para>
/// A tick leases a whole batch up front and then dispatches the rows one at a time, so a slow row can be
/// reached long after its lease expired and another instance has LEGITIMATELY reclaimed it. Every write
/// the stale worker then makes is a write against someone else's row: it can wipe the new owner's lease
/// mid-dispatch, mark Poisoned a row the new owner already dispatched successfully (corrupting the "an
/// audit event was lost" operator signal), or inflate the attempt count toward premature poisoning.
/// </para>
/// The takeover is injected through the dispatcher's own <see cref="TimeProvider"/>, which it consults at
/// exactly the point a real lease expiry would bite: after the row has been read, before it is mutated.
/// </summary>
public sealed class AuditOutboxStaleClaimTests : IClassFixture<PostgresFixture>
{
    private const string NewOwner = "instance-b";

    private readonly PostgresFixture _fixture;

    public AuditOutboxStaleClaimTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_stale_worker_does_not_mark_dispatched_a_row_another_instance_has_reclaimed()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();
        var id = await SeedPendingMessageAsync(rackId: null);

        var newOwnerLeaseUntil = DateTime.UtcNow.AddMinutes(10);

        // Fires after the row has been read and immediately before it is marked Dispatched — the moment a
        // lease that expired earlier in the batch lets another instance take the row over for real.
        var time = new HookingTimeProvider(
            DateTimeOffset.UtcNow, fireOnCall: 2, () => TakeOverRow(id, newOwnerLeaseUntil));
        var dispatcher = AuditOutboxDispatcherTestFactory.Create(
            _fixture, time, new AuditDurabilityOptions { OutboxLeaseSeconds = 1 });

        await dispatcher.TickAsync(default);

        time.Fired.Should().BeTrue("the test must actually have simulated the takeover");

        await using var verify = _fixture.CreateContext();
        var message = await verify.AuditOutboxMessages.SingleAsync(m => m.Id == id);

        message.ClaimedBy.Should().Be(
            NewOwner, "a stale worker must not wipe the lease of the instance that legitimately owns the row now");
        message.Status.Should().Be(
            AuditOutboxStatus.Pending, "only the current owner may move the row to a terminal status");
        message.LeaseUntilUtc.Should().BeCloseTo(newOwnerLeaseUntil, TimeSpan.FromSeconds(1));
        message.DispatchedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_stale_worker_does_not_release_or_reschedule_a_row_another_instance_has_reclaimed()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();

        // A rack_id referencing no Rack row: topology_audit_event's FK makes every dispatch attempt fail,
        // driving this row down the failure/retry path rather than the success path.
        var id = await SeedPendingMessageAsync(rackId: Guid.NewGuid());

        DateTime availableAtBefore;
        await using (var before = _fixture.CreateContext())
        {
            availableAtBefore = (await before.AuditOutboxMessages.SingleAsync(m => m.Id == id)).AvailableAtUtc;
        }

        var newOwnerLeaseUntil = DateTime.UtcNow.AddMinutes(10);

        // Fires after the failure handler has read the row and before it reschedules it.
        var time = new HookingTimeProvider(
            DateTimeOffset.UtcNow, fireOnCall: 2, () => TakeOverRow(id, newOwnerLeaseUntil));
        var dispatcher = AuditOutboxDispatcherTestFactory.Create(
            _fixture, time, new AuditDurabilityOptions { OutboxLeaseSeconds = 1, OutboxMaxAttempts = 5 });

        await dispatcher.TickAsync(default);

        time.Fired.Should().BeTrue("the test must actually have simulated the takeover");

        await using var verify = _fixture.CreateContext();
        var message = await verify.AuditOutboxMessages.SingleAsync(m => m.Id == id);

        message.ClaimedBy.Should().Be(
            NewOwner, "a stale worker's retry bookkeeping must not clear the new owner's claim mid-dispatch");
        message.LeaseUntilUtc.Should().BeCloseTo(newOwnerLeaseUntil, TimeSpan.FromSeconds(1));
        message.AvailableAtUtc.Should().BeCloseTo(
            availableAtBefore, TimeSpan.FromSeconds(1),
            "a stale worker must not push out the availability of a row the new owner is dispatching right now");
    }

    /// <summary>Simulates another dispatcher instance legitimately re-claiming the row after lease expiry.</summary>
    private void TakeOverRow(Guid id, DateTime leaseUntilUtc)
        => Task.Run(async () =>
        {
            await using var context = _fixture.CreateContext();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE audit_outbox SET claimed_by = {1}, lease_until_utc = {2}, attempt_count = attempt_count + 1 WHERE id = {0}",
                id, NewOwner, leaseUntilUtc);
        }).GetAwaiter().GetResult();

    /// <summary>The dispatcher's claim query is global (not scoped to specific ids); clear leftovers first.</summary>
    private async Task ResetAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM audit_outbox;");
    }

    private async Task<Guid> SeedPendingMessageAsync(Guid? rackId)
    {
        var id = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.AuditOutboxMessages.Add(new AuditOutboxMessage(
            id, DateTime.UtcNow, ActorType.System, "system", "test.action", "test-target", targetId: null,
            correlationId: Guid.NewGuid(), result: "success", rackId: rackId, snapshotId: null,
            detailsJson: null, availableAtUtc: DateTime.UtcNow));
        await context.SaveChangesAsync();
        return id;
    }
}
