using System.Security.Claims;
using Caisson.Api.Security;
using Caisson.Domain.Enums;

namespace Caisson.Api.Auditing;

/// <summary>
/// The single shared actor-resolution rule for every audit writer/handler (story #308), extracted from
/// what used to be three verbatim copies (<c>AuditEventWriter.ResolveActor</c>,
/// <c>ChannelAuditEventWriter.ResolveActor</c>, and <see cref="Caisson.Api.Security.ForbidLoggingAuthorizationResultHandler"/>'s
/// inline subject lookup).
/// </summary>
public static class AuditActorResolver
{
    /// <summary>Resolves the audit actor kind + stable id from the current principal's claims.</summary>
    public static (ActorType ActorType, string ActorId) Resolve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var actorId = ResolveActorId(user);
        var actorType = user.IsInRole(CaissonRoles.ServiceAccount) ? ActorType.ServiceAccount : ActorType.User;
        return (actorType, actorId);
    }

    /// <summary>Resolves just the stable subject/actor id (e.g. for a log line that doesn't need the actor kind).</summary>
    public static string ResolveActorId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.FindFirstValue("oid")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.Identity?.Name
            ?? "unknown";
    }
}
