using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A test-only authentication handler that injects the identity/roles a test declares via headers — no
/// real Entra tenant is needed (standard ASP.NET Core integration-test practice). A request with no
/// <c>X-Test-User</c> header is treated as anonymous (→ 401 via the fallback policy); otherwise the
/// declared <c>X-Test-Roles</c> (comma-separated) become <c>roles</c> claims so <c>RequireRole</c>
/// evaluates exactly as it would for a real token.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The test authentication scheme name.</summary>
    public const string SchemeName = "Test";

    /// <summary>Header naming the caller (its presence marks the request authenticated).</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Header carrying the caller's comma-separated roles.</summary>
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.ToString()),
            new("oid", user.ToString()),
            new("name", user.ToString()),
        };

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("roles", role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
