using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Auditing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 1 contract's central invariant (story #308 AC1): <see cref="MandatoryAuditOutbox"/>
/// only stages the row — it never calls <c>SaveChangesAsync</c> itself, so the caller's own mutation
/// commit is what makes the audit row and the mutation atomic.
/// </summary>
public sealed class MandatoryAuditOutboxTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MandatoryAuditOutboxTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Add_stages_the_row_as_a_pending_change_without_saving()
    {
        await _fixture.MigrateAsync();

        var outbox = new MandatoryAuditOutbox();
        var envelope = new AuditEventEnvelope(
            ActorType.User, "actor-1", "test.action", "test-target", null, Guid.NewGuid(), "success");

        await using var context = _fixture.CreateContext();
        var id = outbox.Add(context, envelope, DateTime.UtcNow);

        context.ChangeTracker.HasChanges().Should().BeTrue();
        context.Entry(context.AuditOutboxMessages.Local.Single()).State.Should().Be(EntityState.Added);

        // Never saved by MandatoryAuditOutbox itself: a fresh context sees nothing yet.
        await using var verify = _fixture.CreateContext();
        (await verify.AuditOutboxMessages.CountAsync(m => m.Id == id)).Should().Be(0);

        // The caller's own commit is what persists it (proves the row IS a normal tracked change).
        await context.SaveChangesAsync();
        (await verify.AuditOutboxMessages.CountAsync(m => m.Id == id)).Should().Be(1);
    }

    [Fact]
    public async Task Add_returns_the_id_that_becomes_the_dispatched_audit_event_id()
    {
        await _fixture.MigrateAsync();

        var outbox = new MandatoryAuditOutbox();
        var envelope = new AuditEventEnvelope(
            ActorType.ServiceAccount, "svc-1", "network-intent.saved", "rack-network-intent",
            "target-1", Guid.NewGuid(), "success", RackId: Guid.NewGuid());

        await using var context = _fixture.CreateContext();
        var id = outbox.Add(context, envelope, DateTime.UtcNow);
        await context.SaveChangesAsync();

        await using var verify = _fixture.CreateContext();
        var saved = await verify.AuditOutboxMessages.SingleAsync(m => m.Id == id);
        saved.Action.Should().Be("network-intent.saved");
        saved.Status.Should().Be(Caisson.Domain.Auditing.AuditOutboxStatus.Pending);
    }
}
