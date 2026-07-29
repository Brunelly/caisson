namespace Caisson.Api.Security;

/// <summary>
/// The canonical Caisson roles (story #7). OIDC/Entra group and app-role claims are mapped onto these
/// by <see cref="RoleClaimsTransformation"/>; there is no custom identity system. All read endpoints are
/// viewable by <see cref="ReadOnly"/> and above.
/// </summary>
public static class CaissonRoles
{
    /// <summary>Full access, including complete history and audit trail.</summary>
    public const string Admin = "Admin";

    /// <summary>Operational access to snapshots, entity history and audit.</summary>
    public const string Operator = "Operator";

    /// <summary>Read-only access to snapshots, entity history and audit.</summary>
    public const string ReadOnly = "ReadOnly";

    /// <summary>A non-interactive service principal (e.g. the UI backend or automation).</summary>
    public const string ServiceAccount = "ServiceAccount";

    /// <summary>
    /// An elevated, independently-revocable permission (story #65, AC1) that authorizes applying a single
    /// drift correction (driving the RouterOS write driver). Deliberately NOT included in <see cref="All"/>:
    /// holding only this value grants no read/operator-viewing access, and an Operator who lacks it must
    /// still be rejected — the story's explicit "distinct from read-only/operator viewing" requirement.
    /// </summary>
    public const string DriftApply = "DriftApply";

    /// <summary>Every role permitted to read topology/audit data.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Admin, Operator, ReadOnly, ServiceAccount };

    /// <summary>Roles permitted to trigger/cancel discovery runs (story #8, AC2/NFR3).</summary>
    public static readonly IReadOnlyList<string> Operators = new[] { Admin, Operator };

    /// <summary>
    /// Every value a <c>Authentication:RoleMappings</c> entry may legally target (story #65): the viewing
    /// roles in <see cref="All"/> plus the elevated <see cref="DriftApply"/> permission. Used only by
    /// <see cref="RoleClaimsTransformation.ValidateMappings"/>'s fail-closed canonical-target check —
    /// <see cref="DriftApply"/> is intentionally absent from <see cref="All"/> itself.
    /// </summary>
    public static readonly IReadOnlyList<string> AllMappableTargets = new[] { Admin, Operator, ReadOnly, ServiceAccount, DriftApply };
}
