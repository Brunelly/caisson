namespace Caisson.Infrastructure.Persistence.Auditing;

/// <summary>
/// The Tier 1 (mandatory-durable) audit seam (story #308, ADR 0064): stages an <see cref="Domain.Auditing.AuditOutboxMessage"/>
/// onto the caller's own <see cref="CaissonDbContext"/> so it commits in the SAME transaction as the state
/// mutation it records. Lives in <c>Caisson.Infrastructure</c> — not <c>Caisson.Api</c> — because
/// <c>Caisson.Orchestration</c>, <c>Caisson.Ingestion</c> and <c>Caisson.Infrastructure</c> itself all need
/// to resolve it but none of them may reference <c>Caisson.Api</c>.
/// </summary>
public interface IMandatoryAuditOutbox
{
    /// <summary>
    /// Adds a pending outbox row to <paramref name="context"/>. Deliberately does NOT call
    /// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/> —
    /// the mutation owner keeps the single commit, so a rolled-back transaction can never leave an orphan
    /// audit row and a committed mutation can never be missing one. Returns the message id, which is also
    /// the eventual <see cref="Domain.Topology.TopologyAuditEvent"/> id.
    /// </summary>
    Guid Add(CaissonDbContext context, AuditEventEnvelope envelope, DateTime occurredAtUtc);
}
