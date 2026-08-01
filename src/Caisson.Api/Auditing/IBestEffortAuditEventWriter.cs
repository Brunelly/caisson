using System.Security.Claims;

namespace Caisson.Api.Auditing;

/// <summary>
/// The explicit Tier 3 (best-effort) audit seam (story #308, ADR 0064): high-volume read auditing, kept
/// off the request path via a bounded, explicitly droppable channel. This is the ONLY writer that may
/// shed events under load — Tier 1 (<see cref="Caisson.Infrastructure.Persistence.Auditing.IMandatoryAuditOutbox"/>)
/// and Tier 2 (<see cref="IAuthorizationDenialAuditWriter"/>) are separate, non-droppable seams, so a Tier
/// 1/2 event can never reach this channel by accident.
/// <para>
/// This interface has the same shape as the legacy <see cref="IAuditEventWriter"/> it supersedes (both are
/// implemented by the same channel-backed writer during the migration to explicit tiers); callers still on
/// <see cref="IAuditEventWriter"/> are reclassified onto this seam (or Tier 1/2) as each call site is audited.
/// </para>
/// </summary>
public interface IBestEffortAuditEventWriter
{
    /// <summary>Records a best-effort read-audit event.</summary>
    Task WriteReadAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        CancellationToken cancellationToken);

    /// <summary>Records a best-effort action-audit event with an explicit result.</summary>
    Task WriteActionAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        string result, CancellationToken cancellationToken, string? detailsJson = null);
}
