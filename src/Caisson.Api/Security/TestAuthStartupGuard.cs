using Microsoft.Extensions.Hosting;

namespace Caisson.Api.Security;

/// <summary>
/// The fail-closed gate for the environment-gated test-auth scheme (ADR 0018): a host that has
/// <c>Testing:EnableTestAuth</c> set REFUSES TO BOOT (throws, not merely rejects requests) under any
/// <c>ASPNETCORE_ENVIRONMENT</c> other than Development or Testing. Called once, immediately after
/// <c>builder.Environment</c> is available and before any service registration, so a misconfigured
/// production/staging deployment can never come up with the synthetic principal active.
/// </summary>
public static class TestAuthStartupGuard
{
    /// <summary>
    /// Validates the flag against the host environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="enableTestAuth"/> is <c>true</c> and <paramref name="environment"/> is
    /// neither Development nor Testing.
    /// </exception>
    public static void Validate(IHostEnvironment environment, bool enableTestAuth)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!enableTestAuth)
        {
            return;
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Testing:EnableTestAuth is true under ASPNETCORE_ENVIRONMENT='{environment.EnvironmentName}'. " +
            "The environment-gated test-auth scheme (ADR 0018) is only permitted under Development or " +
            "Testing — refusing to start rather than risk minting the synthetic principal in production.");
    }
}
