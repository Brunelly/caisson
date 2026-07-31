namespace Caisson.Ingestion.Security;

/// <summary>
/// The local/CI/test <see cref="IGitCredentialProvider"/>: reads the GitHub token from the
/// <c>CAISSON_GITHUB_TOKEN</c> environment variable so tests and local runs never touch Azure. Unlike the
/// webhook-secret resolver, there is no fixed development fallback — a write-capable git credential must never
/// have a shared default — so an unset variable always fails closed with
/// <see cref="GitCredentialUnavailableException"/> (and the message notes that hosted environments must use
/// Key Vault, not this provider).
/// </summary>
public sealed class EnvGitCredentialProvider : IGitCredentialProvider
{
    /// <summary>The environment variable the GitHub token is read from.</summary>
    public const string TokenEnvVar = "CAISSON_GITHUB_TOKEN";

    /// <inheritdoc />
    public Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (string.IsNullOrEmpty(token))
        {
            var suffix = IsProductionEnvironment()
                ? " Hosted environments must resolve the token from Key Vault via managed identity, not this "
                  + "environment provider."
                : string.Empty;
            throw new GitCredentialUnavailableException(
                $"{TokenEnvVar} is not set; no GitHub credential is available.{suffix}");
        }

        return Task.FromResult(new GitHubCredential(token));
    }

    private static bool IsProductionEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
    }
}
