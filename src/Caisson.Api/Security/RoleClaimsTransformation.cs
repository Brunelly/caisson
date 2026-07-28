using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

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

    /// <inheritdoc />
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in identity.Claims)
        {
            if (claim.Type is not (RoleClaimType or GroupsClaimType))
            {
                continue;
            }

            if (_mappings.TryGetValue(claim.Value, out var mapped))
            {
                canonical.Add(mapped);
            }
            else if (CaissonRoles.All.Contains(claim.Value, StringComparer.Ordinal))
            {
                canonical.Add(claim.Value);
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
