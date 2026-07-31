namespace Caisson.Ingestion.Security;

/// <summary>
/// Resolves the GitHub credential used to author desired-state pull requests (story #172, Task #205),
/// mirroring the <c>IGitIngestionSecretsResolver</c> seam so the token is never bound into an
/// <c>IOptions</c> POCO, serialized, or logged. The default hosted implementation is
/// <c>KeyVaultGitCredentialProvider</c> (managed identity, AC4); the local/CI/test default is
/// <c>EnvGitCredentialProvider</c>. PAT-first for v1; the interface is stable so a future
/// <c>GitHubAppCredentialProvider</c> (installation token) can be swapped in with no call-site change
/// (story Q3).
/// </summary>
public interface IGitCredentialProvider
{
    /// <summary>
    /// Resolves the current GitHub credential. Throws <see cref="GitCredentialUnavailableException"/> when no
    /// credential can be resolved (fail-closed); callers surface that as a stable
    /// <c>GIT_CREDENTIALS_UNAVAILABLE</c> error code with no secret text.
    /// </summary>
    Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a GitHub credential cannot be resolved (missing Key Vault config, managed-identity failure,
/// or a fail-closed dev/CI environment with no configured token). Carries NO secret text.
/// </summary>
public sealed class GitCredentialUnavailableException : Exception
{
    public GitCredentialUnavailableException(string message)
        : base(message)
    {
    }

    public GitCredentialUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
