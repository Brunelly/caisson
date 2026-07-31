using Caisson.Api.Options;
using Microsoft.Extensions.Hosting;

namespace Caisson.Api.Startup;

/// <summary>
/// The fail-closed gate for the desired-state GitHub PR feature (story #172, Task #205; AC4), mirroring
/// <c>GitIngestionStartupGuard</c>/<c>JwtAuthorityStartupGuard</c>'s "refuse to boot rather than run
/// misconfigured" shape. Called once at startup after options binding. When the feature is enabled it requires
/// the repository (owner/name) to be configured; and in Production it additionally requires a resolvable Key
/// Vault URI + secret name (PAT mode) or App configuration — there is NO static/env PAT fallback in hosted
/// environments, so a production deployment can never come up pointing at a non-existent credential source or
/// silently fall back to an environment token.
/// </summary>
public static class GitPrStartupGuard
{
    /// <summary>
    /// Validates the PR feature configuration against the host environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the feature is enabled but the repository is unconfigured, or when it is enabled in
    /// Production without a resolvable Key Vault credential source.
    /// </exception>
    public static void Validate(IHostEnvironment environment, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.RepoOwner) || string.IsNullOrWhiteSpace(options.RepoName))
        {
            throw new InvalidOperationException(
                $"{GitHubOptions.SectionName}:Enabled is true but RepoOwner/RepoName are not configured — "
                + "refusing to start the PR feature without a target repository.");
        }

        if (!environment.IsProduction())
        {
            return;
        }

        if (options.AuthMode == GitPrAuthMode.Pat
            && (string.IsNullOrWhiteSpace(options.KeyVaultUri) || string.IsNullOrWhiteSpace(options.PatSecretName)))
        {
            throw new InvalidOperationException(
                $"{GitHubOptions.SectionName}:Enabled is true under ASPNETCORE_ENVIRONMENT="
                + $"'{environment.EnvironmentName}' but no Key Vault URI + secret name is configured — refusing "
                + "to start with a hosted PR feature and no managed-identity credential source (no static/env "
                + "PAT fallback is permitted in Production).");
        }
    }
}
