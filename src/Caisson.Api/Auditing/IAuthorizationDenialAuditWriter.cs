using Caisson.Domain.Enums;

namespace Caisson.Api.Auditing;

/// <summary>
/// The Tier 2 (durable-first-N + bounded counter) audit seam (story #308, ADR 0064) for authorization
/// denials and anything an unauthorized caller can trigger at will. Implemented by
/// <see cref="AuthorizationDenialAuditWriter"/>: the first <c>DenialFirstN</c> distinct denials per
/// <c>(actorId, endpoint, outcome, window)</c> bucket are written durably and immediately (surviving an
/// ungraceful restart); the rest are counted in memory by <see cref="DenialOverflowAccumulator"/> and
/// flushed periodically by <see cref="AuditDenialFlushService"/> as a single bounded aggregate row.
/// </summary>
public interface IAuthorizationDenialAuditWriter
{
    /// <summary>
    /// Records one denial. Never throws for a persistence failure — callers (the authorization result
    /// handler) must still return their 403 even if this fails; implementations log and swallow.
    /// </summary>
    /// <param name="endpoint">
    /// The STABLE bucket key: <c>"{httpMethod} {routeTemplate}"</c> (e.g. <c>"PUT /api/racks/{rackId}/network-intent"</c>).
    /// NEVER the raw request path or query string — those are caller-controlled and would make bucket
    /// cardinality (and so write volume) attacker-controlled.
    /// </param>
    /// <param name="outcome">The denial outcome code (e.g. <c>"403"</c>).</param>
    Task RecordDenialAsync(
        ActorType actorType,
        string actorId,
        string endpoint,
        string outcome,
        Guid? rackId,
        Guid correlationId,
        string? detailsJson,
        CancellationToken cancellationToken);
}
