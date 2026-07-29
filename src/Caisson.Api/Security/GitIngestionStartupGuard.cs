using Caisson.Ingestion.Security;
using Microsoft.Extensions.Hosting;

namespace Caisson.Api.Security;

/// <summary>
/// The fail-closed gate for Git desired-state ingestion (story #62, AC5), mirroring
/// <see cref="JwtAuthorityStartupGuard"/>'s "refuse to boot rather than run misconfigured" shape. Called
/// once at startup, after options binding, so a deployment with ingestion enabled and no resolvable
/// webhook secret can never come up and silently accept unsigned webhook deliveries.
/// </summary>
public static class GitIngestionStartupGuard
{
    /// <summary>
    /// Validates that a webhook secret resolves whenever ingestion is enabled.
    /// <see cref="IGitIngestionSecretsResolver"/> already encodes the env-var + fail-closed-in-Production
    /// fallback (ADR 0026), so this guard only needs to ask it once, up front.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="ingestionEnabled"/> is <c>true</c> but no webhook secret resolves.
    /// </exception>
    public static void Validate(IHostEnvironment environment, bool ingestionEnabled, IGitIngestionSecretsResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(secrets);

        if (!ingestionEnabled)
        {
            return;
        }

        if (!secrets.TryResolveWebhookSecret(out _))
        {
            throw new InvalidOperationException(
                $"GitIngestion:Enabled is true but {EnvGitIngestionSecretsResolver.SecretEnvVar} is not " +
                $"configured under ASPNETCORE_ENVIRONMENT='{environment.EnvironmentName}' — refusing to " +
                "start with webhook ingestion enabled and no signature secret to verify deliveries against.");
        }
    }
}
