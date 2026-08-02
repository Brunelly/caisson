namespace Caisson.Domain.Auditing;

/// <summary>The dispatch lifecycle of an <see cref="AuditOutboxMessage"/> (story #308, ADR 0064).</summary>
public enum AuditOutboxStatus
{
    /// <summary>Not yet dispatched to <c>topology_audit_event</c>; claimable once <c>available_at_utc</c> elapses.</summary>
    Pending = 0,

    /// <summary>
    /// Successfully projected to <c>topology_audit_event</c> (same id) in the dispatcher's transaction.
    /// Terminal — never reverted.
    /// </summary>
    Dispatched,

    /// <summary>
    /// Exhausted <c>OutboxMaxAttempts</c> retries. Terminal for automatic dispatch: the row is retained in
    /// full (never deleted) with a stable, sanitized <see cref="AuditOutboxMessage.FailureCode"/> for
    /// operator triage, but it is never marked <see cref="Dispatched"/> by any code path.
    /// </summary>
    Poisoned,
}
