using System.Security.Claims;

namespace Caisson.Api.Security;

/// <summary>
/// The per-rack authorization seam (finding #29). Today's RBAC is role-only — any principal holding a
/// recognised role (<see cref="CaissonRoles.All"/>) may read any rack, with no per-rack ACL — and this
/// interface exists to make a FUTURE per-rack restriction a one-class change rather than a
/// controller-by-controller retrofit. The shipped <see cref="AllowAllRackAccessPolicy"/> is genuinely
/// additive: it changes no current behaviour. A future implementation that denies access MUST return
/// <c>false</c> rather than throw, and callers MUST turn a denial into 404 (never 403) so rack existence
/// is never an oracle for a caller without access to it (documented deviation: full per-rack ACL
/// enforcement is deferred — see ADR 0023, cross-referencing ADR 0012).
/// </summary>
public interface IRackAccessPolicy
{
    /// <summary>Whether <paramref name="user"/> may read data for <paramref name="rackId"/>.</summary>
    Task<bool> CanReadAsync(ClaimsPrincipal user, Guid rackId, CancellationToken cancellationToken);
}

/// <summary>
/// The default, allow-all <see cref="IRackAccessPolicy"/> — role-only RBAC is still enforced by the
/// existing <c>[Authorize(Policy = AuthorizationPolicies.TopologyRead)]</c> gates; this seam adds no
/// restriction beyond that today.
/// </summary>
public sealed class AllowAllRackAccessPolicy : IRackAccessPolicy
{
    /// <inheritdoc />
    public Task<bool> CanReadAsync(ClaimsPrincipal user, Guid rackId, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
