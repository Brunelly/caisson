using System.Security.Claims;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;

namespace Caisson.Api.Auditing;

/// <summary>Records an API-access audit event for an auditable read (AC3).</summary>
public interface IAuditEventWriter
{
    /// <summary>
    /// Appends a <see cref="TopologyAuditEvent"/> describing a read the caller performed, stamped with
    /// the request correlation id and the caller's identity.
    /// </summary>
    Task WriteReadAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal API-access audit writer (AC3, NFR4): a single insert on the indexed, append-only audit
/// table. It is intentionally small and sits behind <see cref="ICorrelationContext"/> so it can be made
/// asynchronous/off-request later if the NFR2 P95 &lt; 500 ms budget is threatened.
/// </summary>
public sealed class AuditEventWriter : IAuditEventWriter
{
    private readonly CaissonDbContext _context;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _time;

    public AuditEventWriter(CaissonDbContext context, ICorrelationContext correlation, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task WriteReadAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var (actorType, actorId) = ResolveActor(user);
        var audit = new TopologyAuditEvent(
            Guid.NewGuid(),
            _time.GetUtcNow().UtcDateTime,
            actorType,
            actorId,
            action,
            targetType,
            _correlation.CorrelationId,
            result: "success",
            rackId: rackId,
            snapshotId: null,
            targetId: targetId);

        _context.AuditEvents.Add(audit);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static (ActorType ActorType, string ActorId) ResolveActor(ClaimsPrincipal user)
    {
        var actorId =
            user.FindFirstValue("oid")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.Identity?.Name
            ?? "unknown";

        var actorType = user.IsInRole(CaissonRoles.ServiceAccount) ? ActorType.ServiceAccount : ActorType.User;
        return (actorType, actorId);
    }
}
