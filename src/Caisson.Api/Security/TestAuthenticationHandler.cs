using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Security;

/// <summary>
/// The environment-gated test-auth scheme (ADR 0018), active only when <c>Testing:EnableTestAuth</c> is
/// true (fail-closed outside Development/Testing — see <see cref="TestAuthStartupGuard"/>). Every request
/// is "authenticated" as the SAME fixed, clearly-labelled, non-privileged synthetic principal — subject
/// <see cref="Subject"/>, holding only <see cref="CaissonRoles.ReadOnly"/> — so the Playwright e2e smoke
/// can exercise the real, running <c>Caisson.Api</c> in CI without a real Entra tenant. There is
/// deliberately no way to mint any other subject or role through this handler: both are fixed code
/// constants, never sourced from a header, query string, or config value.
/// </summary>
public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The test-auth scheme name.</summary>
    public const string SchemeName = "CaissonTestAuth";

    /// <summary>The fixed, clearly-labelled subject every request is authenticated as.</summary>
    public const string Subject = "caisson-ci-e2e";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Subject),
            new Claim("oid", Subject),
            new Claim("name", Subject),
            new Claim(RoleClaimsTransformation.RoleClaimType, CaissonRoles.ReadOnly),
        };

        var identity = new ClaimsIdentity(
            claims, SchemeName, nameType: "name", roleType: RoleClaimsTransformation.RoleClaimType);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
