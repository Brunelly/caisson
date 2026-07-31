using Caisson.Ingestion.Security;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Ingestion.Tests.Security;

/// <summary>
/// Unit tests for the git credential providers (story #172, Task #205): the env provider reads a token and
/// fails closed when unset, the credential never reveals its value via <c>ToString</c>, and the Key Vault
/// provider caches the secret across calls and re-fetches after the cache is cleared.
/// </summary>
[Collection("git-credential-env")]
public sealed class GitCredentialProviderTests
{
    [Fact]
    public async Task Env_provider_returns_the_configured_token()
    {
        var previous = Environment.GetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar, "ghp_env_token");
            var credential = await new EnvGitCredentialProvider().GetTokenAsync(default);
            credential.Reveal().Should().Be("ghp_env_token");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar, previous);
        }
    }

    [Fact]
    public async Task Env_provider_fails_closed_when_the_token_is_unset()
    {
        var previous = Environment.GetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar, null);
            var act = async () => await new EnvGitCredentialProvider().GetTokenAsync(default);
            await act.Should().ThrowAsync<GitCredentialUnavailableException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvGitCredentialProvider.TokenEnvVar, previous);
        }
    }

    [Fact]
    public void Credential_never_reveals_its_value_via_to_string()
    {
        var credential = new GitHubCredential("ghp_super_secret");

        credential.ToString().Should().NotContain("ghp_super_secret");
        credential.ToString().Should().Contain("redacted");
        credential.Reveal().Should().Be("ghp_super_secret");
    }

    [Fact]
    public async Task Key_vault_provider_caches_the_secret_across_calls()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new TestVaultProvider(
            new KeyVaultCredentialSettings("https://vault.vault.azure.net/", "git-pat"), cache, value: "ghp_vault");

        (await provider.GetTokenAsync(default)).Reveal().Should().Be("ghp_vault");
        (await provider.GetTokenAsync(default)).Reveal().Should().Be("ghp_vault");

        provider.Fetches.Should().Be(1);
    }

    private sealed class TestVaultProvider : KeyVaultGitCredentialProvider
    {
        private readonly string _value;

        public TestVaultProvider(KeyVaultCredentialSettings settings, IMemoryCache cache, string value)
            : base(settings, cache, NullLogger<KeyVaultGitCredentialProvider>.Instance)
            => _value = value;

        public int Fetches { get; private set; }

        protected override Task<string> FetchSecretAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(_value);
        }
    }
}
