using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Auditing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves story #308 AC1 against real PostgreSQL: a mutation and its Tier 1 outbox row commit together in
/// one transaction, and a rolled-back mutation leaves no orphan outbox row.
/// </summary>
public sealed class AuditOutboxTransactionalityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditOutboxTransactionalityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Mutation_and_outbox_row_commit_together_in_one_transaction()
    {
        await _fixture.MigrateAsync();

        var outbox = new MandatoryAuditOutbox();
        var rackId = Guid.NewGuid();
        Guid outboxId;

        await using (var context = _fixture.CreateContext())
        {
            context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
            var envelope = new AuditEventEnvelope(
                ActorType.User, "actor-1", "network-intent.saved", "rack", rackId.ToString(),
                Guid.NewGuid(), "success", RackId: rackId);
            outboxId = outbox.Add(context, envelope, DateTime.UtcNow);

            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        (await verify.Racks.CountAsync(r => r.Id == rackId)).Should().Be(1);
        (await verify.AuditOutboxMessages.CountAsync(m => m.Id == outboxId)).Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_the_mutation_nor_the_outbox_row()
    {
        await _fixture.MigrateAsync();

        var outbox = new MandatoryAuditOutbox();
        var rackId = Guid.NewGuid();
        Guid outboxId;

        await using (var context = _fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
            var envelope = new AuditEventEnvelope(
                ActorType.User, "actor-1", "network-intent.saved", "rack", rackId.ToString(),
                Guid.NewGuid(), "success", RackId: rackId);
            outboxId = outbox.Add(context, envelope, DateTime.UtcNow);

            await context.SaveChangesAsync();

            // Simulates a mutation whose overall transaction fails for an unrelated reason (e.g. a
            // concurrency conflict elsewhere) AFTER both the mutation and the audit row were staged.
            await transaction.RollbackAsync();
        }

        await using var verify = _fixture.CreateContext();
        (await verify.Racks.CountAsync(r => r.Id == rackId)).Should().Be(0);
        (await verify.AuditOutboxMessages.CountAsync(m => m.Id == outboxId)).Should().Be(0);
    }
}
