namespace Caisson.Api.Security;

/// <summary>Named authorization policies applied to the read-only topology/audit endpoints.</summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Requires one of the recognised read roles (<see cref="CaissonRoles.All"/>). Applied to every
    /// controller: an authenticated caller with no recognised role gets 403; anonymous callers get 401
    /// from the fallback policy.
    /// </summary>
    public const string TopologyRead = "TopologyRead";
}
