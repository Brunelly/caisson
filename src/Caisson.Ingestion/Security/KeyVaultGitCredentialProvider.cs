using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Security;

/// <summary>Non-secret settings for the Key Vault credential provider (Key Vault URI + secret NAME, never a value).</summary>
public sealed record KeyVaultCredentialSettings(string KeyVaultUri, string SecretName);

/// <summary>
/// The hosted <see cref="IGitCredentialProvider"/> (story #172, Task #205; AC4): fetches the GitHub PAT from
/// Azure Key Vault at runtime using managed identity (<see cref="DefaultAzureCredential"/>) — no secret in
/// appsettings, source, options POCO, or logs. A short-TTL in-memory cache means the secret is not fetched on
/// every request (meeting the ≤8s create / ≤3s reuse budgets and avoiding Key Vault throttling); the cache is
/// cleared on any auth/fetch failure so a rotated or revoked credential is re-fetched. On failure it throws
/// <see cref="GitCredentialUnavailableException"/> with no secret text; the publisher surfaces
/// <c>GIT_CREDENTIALS_UNAVAILABLE</c>.
/// <para>
/// <see cref="FetchSecretAsync"/> is the single Key-Vault-touching seam (overridable for tests so the caching
/// / redaction behaviour can be exercised without Azure).
/// </para>
/// </summary>
public class KeyVaultGitCredentialProvider : IGitCredentialProvider
{
    /// <summary>Short cache lifetime balancing rotation latency against per-request Key Vault calls.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string CacheKey = "caisson.git.pr.pat";

    private readonly KeyVaultCredentialSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<KeyVaultGitCredentialProvider> _logger;
    private readonly Lazy<SecretClient> _client;

    public KeyVaultGitCredentialProvider(
        KeyVaultCredentialSettings settings,
        IMemoryCache cache,
        ILogger<KeyVaultGitCredentialProvider> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrEmpty(_settings.KeyVaultUri) || string.IsNullOrEmpty(_settings.SecretName))
        {
            throw new ArgumentException("Key Vault URI and secret name are required.", nameof(settings));
        }

        _client = new Lazy<SecretClient>(
            () => new SecretClient(new Uri(_settings.KeyVaultUri), new DefaultAzureCredential()));
    }

    /// <inheritdoc />
    public async Task<GitHubCredential> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<string>(CacheKey, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return new GitHubCredential(cached);
        }

        string secret;
        try
        {
            secret = await FetchSecretAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException)
        {
            // Clear any stale cache and surface a secret-free failure; the full exception is logged (no value).
            _cache.Remove(CacheKey);
            _logger.LogError(ex, "Failed to retrieve the GitHub credential from Key Vault '{VaultUri}'.", _settings.KeyVaultUri);
            throw new GitCredentialUnavailableException(
                "The GitHub credential could not be retrieved from Key Vault.", ex);
        }

        if (string.IsNullOrEmpty(secret))
        {
            _cache.Remove(CacheKey);
            throw new GitCredentialUnavailableException("The GitHub credential retrieved from Key Vault was empty.");
        }

        _cache.Set(CacheKey, secret, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });
        return new GitHubCredential(secret);
    }

    /// <summary>Fetches the secret VALUE from Key Vault. Overridable so tests can exercise caching without Azure.</summary>
    protected virtual async Task<string> FetchSecretAsync(CancellationToken cancellationToken)
    {
        KeyVaultSecret secret = await _client.Value.GetSecretAsync(_settings.SecretName, cancellationToken: cancellationToken);
        return secret.Value;
    }
}
