using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;

namespace Caisson.Api.Security;

/// <summary>
/// Maps the OIDC/Entra group and app-role claims present on an authenticated principal onto the
/// canonical <see cref="CaissonRoles"/> via a config-driven <c>Authentication:RoleMappings</c>
/// dictionary (Entra group id / app role → canonical role). Values that are already canonical role
/// names are accepted as-is. Canonical roles are added as claims of the configured role-claim type
/// (<see cref="RoleClaimType"/>) so <c>RequireRole</c> recognises them. There is no custom identity
/// store — this only re-labels claims the identity provider issued.
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    /// <summary>The claim type used for both source app-roles and the emitted canonical roles.</summary>
    public const string RoleClaimType = "roles";

    /// <summary>The claim type Entra emits security-group ids on.</summary>
    public const string GroupsClaimType = "groups";

    private readonly IReadOnlyDictionary<string, string> _mappings;

    public RoleClaimsTransformation(IReadOnlyDictionary<string, string> mappings)
        => _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));

    /// <summary>
    /// Fail-closed startup validation (finding #17), mirroring <see cref="TestAuthStartupGuard"/>: every
    /// configured mapping value must be a genuine canonical role (a typo — e.g. "Admni" — would otherwise
    /// silently mint a claim that never satisfies any <c>RequireRole</c> policy, effectively locking that
    /// mapping's holders out without any startup signal), and outside Development the dictionary must be
    /// non-empty, since an empty map means every deployment would otherwise depend entirely on the
    /// roles-claim passthrough with no group-based grant reachable at all.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a mapping value is not in <see cref="CaissonRoles.All"/>, or when
    /// <paramref name="mappings"/> is empty outside Development.
    /// </exception>
    public static void ValidateMappings(IHostEnvironment environment, IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(mappings);

        foreach (var (source, target) in mappings)
        {
            if (!CaissonRoles.All.Contains(target, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Authentication:RoleMappings maps '{source}' to '{target}', which is not a canonical " +
                    $"Caisson role ({string.Join(", ", CaissonRoles.All)}). Refusing to start rather than " +
                    "silently mint an unrecognised role claim.");
            }
        }

        if (mappings.Count == 0 && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Authentication:RoleMappings is empty under ASPNETCORE_ENVIRONMENT=" +
                $"'{environment.EnvironmentName}'. With no mappings configured, the groups claim can never " +
                "grant a role (finding #17 removed its passthrough) — refusing to start rather than run a " +
                "deployment where no group-based grant is reachable.");
        }
    }

    /// <inheritdoc />
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        // Finding #17: the roles claim (app-role assignment, admin-controlled in Entra) and the groups
        // claim (directory membership, which anyone with group-creation rights can shape) are NOT the
        // same trust class. A canonical role NAME is only ever accepted verbatim from the roles claim;
        // a groups value must resolve through the explicit, reviewed _mappings dictionary — there is no
        // passthrough for groups, so an IdP emitting a directory group literally named "Admin" can never
        // grant Admin on its own.
        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in identity.Claims)
        {
            if (claim.Type == RoleClaimType)
            {
                if (_mappings.TryGetValue(claim.Value, out var mappedRole))
                {
                    canonical.Add(mappedRole);
                }
                else if (CaissonRoles.All.Contains(claim.Value, StringComparer.Ordinal))
                {
                    canonical.Add(claim.Value);
                }
            }
            else if (claim.Type == GroupsClaimType && _mappings.TryGetValue(claim.Value, out var mappedGroup))
            {
                canonical.Add(mappedGroup);
            }
        }

        foreach (var role in canonical)
        {
            // Avoid duplicating a canonical role the principal already carries.
            if (!identity.HasClaim(RoleClaimType, role))
            {
                identity.AddClaim(new Claim(RoleClaimType, role));
            }
        }

        return Task.FromResult(principal);
    }
}
