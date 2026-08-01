using System.Security.Claims;

namespace Caisson.Api.Auditing;

/// <summary>
/// The legacy, not-yet-tier-classified audit seam. Superseded by the three explicit tiers (story #308,
/// ADR 0064): <see cref="Caisson.Infrastructure.Persistence.Auditing.IMandatoryAuditOutbox"/> (Tier 1),
/// <see cref="IAuthorizationDenialAuditWriter"/> (Tier 2), and <see cref="IBestEffortAuditEventWriter"/>
/// (Tier 3). Each remaining call site is being migrated onto its correct tier; once none remain, this
/// interface and its <see cref="Caisson.Api.Auditing.ChannelAuditEventWriter"/> registration are removed.
/// </summary>
public interface IAuditEventWriter
{
    /// <summary>
    /// Appends a <see cref="Caisson.Domain.Topology.TopologyAuditEvent"/> describing a read the caller performed, stamped with
    /// the request correlation id and the caller's identity.
    /// </summary>
    Task WriteReadAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a <see cref="Caisson.Domain.Topology.TopologyAuditEvent"/> for a control-plane write action with an explicit
    /// result. The audit table remains append-only.
    /// </summary>
    /// <param name="detailsJson">
    /// Optional bounded, secret-scrubbed <c>jsonb</c> payload — e.g. the permission used, or a before/after
    /// summary. Additive: existing callers that omit it are unaffected.
    /// </param>
    Task WriteActionAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        string result, CancellationToken cancellationToken, string? detailsJson = null);
}
