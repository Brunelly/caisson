namespace Caisson.Api.Security;

/// <summary>Named authorization policies applied to the read-only topology/audit endpoints.</summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Requires one of the recognised read roles (<see cref="CaissonRoles.All"/>). Applied to every
    /// read controller: an authenticated caller with no recognised role gets 403; anonymous callers get
    /// 401 from the fallback policy.
    /// </summary>
    public const string TopologyRead = "TopologyRead";

    /// <summary>
    /// Requires Admin or Operator (<see cref="CaissonRoles.Operators"/>). Gates the discovery
    /// trigger/cancel endpoints — the only non-GET, control-plane actions (story #8, AC2/NFR3).
    /// </summary>
    public const string DiscoveryTrigger = "DiscoveryTrigger";

    /// <summary>
    /// Requires Admin only. Gates schedule management (enable/interval/jitter) so only administrators
    /// configure recurring discovery (story #8, AC4).
    /// </summary>
    public const string ScheduleManage = "ScheduleManage";

    /// <summary>
    /// Requires the elevated <see cref="CaissonRoles.DriftApply"/> permission (story #65, AC1). Gates the
    /// single-change drift-correction apply endpoint — the first write endpoint in the API — and is
    /// deliberately NOT satisfied by <see cref="CaissonRoles.Operator"/> alone, so an Operator without this
    /// permission is rejected with 403.
    /// </summary>
    public const string DriftApply = "DriftApply";
}
