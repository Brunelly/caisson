using Microsoft.Extensions.Hosting;

namespace Caisson.Api.Security;

/// <summary>
/// The fail-closed gate for the JWT bearer configuration (finding #16), mirroring
/// <see cref="TestAuthStartupGuard"/>'s "refuse to boot rather than run misconfigured" shape. Called
/// once, immediately after <c>builder.Environment</c> is available and before any service registration,
/// so a deployment with an empty/multi-tenant authority or an empty audience can never come up.
/// </summary>
public static class JwtAuthorityStartupGuard
{
    /// <summary>
    /// Validates the configured authority/audience against the host environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown outside Development/Testing when <paramref name="authority"/> is empty, is a multi-tenant
    /// (<c>/common/</c> or <c>/organizations/</c>) endpoint, or when <paramref name="audience"/> is empty.
    /// </exception>
    public static void Validate(IHostEnvironment environment, string? authority, string? audience)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException(
                "AzureAd:Authority is empty under ASPNETCORE_ENVIRONMENT=" +
                $"'{environment.EnvironmentName}'. A tenant-specific authority is required outside " +
                "Development/Testing — refusing to start rather than accept tokens from an unvalidated issuer.");
        }

        if (authority.Contains("/common/", StringComparison.OrdinalIgnoreCase)
            || authority.Contains("/organizations/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AzureAd:Authority ('{authority}') is a multi-tenant endpoint (/common/ or /organizations/) " +
                $"under ASPNETCORE_ENVIRONMENT='{environment.EnvironmentName}'. A tenant-specific authority " +
                "is required outside Development/Testing — refusing to start rather than accept tokens from " +
                "any Microsoft tenant.");
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "AzureAd:Audience is empty under ASPNETCORE_ENVIRONMENT=" +
                $"'{environment.EnvironmentName}'. Refusing to start rather than accept a token intended " +
                "for any audience (today this would only surface as a per-request IDX10208 401).");
        }
    }
}
