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

    /// <summary>Every role permitted to read topology/audit data.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Admin, Operator, ReadOnly, ServiceAccount };
}
