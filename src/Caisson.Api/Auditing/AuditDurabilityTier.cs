namespace Caisson.Api.Auditing;

/// <summary>
/// The three audit durability tiers (story #308, ADR 0064). Every audit event in the codebase is
/// classified into exactly one, explicitly in code — there is no generic "write an audit event" API that
/// could let a Tier 1 or Tier 2 event accidentally reach the droppable Tier 3 channel.
/// </summary>
public enum AuditDurabilityTier
{
    /// <summary>
    /// Mandatory-durable: state mutations (draft create/update, PR create, apply, schedule change, job
    /// terminal transitions including stale/timeout reaper transitions). Written via
    /// <see cref="Caisson.Infrastructure.Persistence.Auditing.IMandatoryAuditOutbox"/> in the same
    /// transaction as the mutation; dispatched at-least-once, idempotent on the outbox row id. Never
    /// dropped, never aggregated.
    /// </summary>
    MandatoryDurable = 1,

    /// <summary>
    /// Durable-first-N + bounded counter: authorization denials and anything an unauthorized caller can
    /// trigger at will. The first N distinct denials per (actor, endpoint, outcome, window) bucket are
    /// written durably and immediately via <see cref="IAuthorizationDenialAuditWriter"/>; the rest are
    /// counted in memory and flushed periodically as a single durable aggregate row.
    /// </summary>
    DurableFirstNBounded = 2,

    /// <summary>
    /// Best-effort: high-volume read auditing. Written via <see cref="IBestEffortAuditEventWriter"/>
    /// (channel-backed, explicitly droppable under load). Kept off the read request path deliberately;
    /// may be shed under load.
    /// </summary>
    BestEffort = 3,
}
