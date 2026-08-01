using Caisson.Domain.Auditing;

namespace Caisson.Infrastructure.Persistence.Auditing;

/// <inheritdoc cref="IMandatoryAuditOutbox"/>
public sealed class MandatoryAuditOutbox : IMandatoryAuditOutbox
{
    /// <inheritdoc />
    public Guid Add(CaissonDbContext context, AuditEventEnvelope envelope, DateTime occurredAtUtc, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        var messageId = id ?? Guid.NewGuid();
        var message = new AuditOutboxMessage(
            messageId,
            occurredAtUtc,
            envelope.ActorType,
            envelope.ActorId,
            envelope.Action,
            envelope.TargetType,
            envelope.TargetId,
            envelope.CorrelationId,
            envelope.Result,
            envelope.RackId,
            envelope.SnapshotId,
            envelope.DetailsJson,
            availableAtUtc: occurredAtUtc);

        // Add only — no SaveChangesAsync. The caller's own single commit (its mutation's SaveChangesAsync)
        // is what makes this row and the mutation atomic (story #308 AC1).
        context.AuditOutboxMessages.Add(message);
        return messageId;
    }
}
