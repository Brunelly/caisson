using Caisson.Api.Options;
using Caisson.Api.Services;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Api.Startup;

/// <summary>
/// DI registration for the desired-state GitHub PR feature (story #172, Task #207). Binds the non-secret
/// <see cref="GitHubOptions"/>, selects the credential provider (Key Vault when configured, else the env
/// provider), registers the typed <see cref="IGitHubPullRequestClient"/> and the idempotency store, and
/// selects <see cref="IDesiredStatePrService"/> — the real <see cref="GitHubDesiredStatePrService"/> when the
/// feature is enabled, else the shipped <see cref="NotYetEnabledDesiredStatePrService"/> stub. Uses
/// <c>TryAdd</c> so tests can override any of these (fake GitHub client / credential provider) before the host
/// builds.
/// </summary>
public static class GitPrServiceCollectionExtensions
{
    public static IServiceCollection AddCaissonGitPr(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GitHubOptions.SectionName);
        services.AddOptions<GitHubOptions>().Bind(section);
        var options = section.Get<GitHubOptions>() ?? new GitHubOptions();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.TryAddScoped<IGitPullRequestLinkStore, GitPullRequestLinkStore>();

        RegisterCredentialProvider(services, options);
        RegisterGitHubClient(services, options);

        if (options.Enabled)
        {
            services.AddScoped<IDesiredStatePrService, GitHubDesiredStatePrService>();
        }
        else
        {
            services.TryAddSingleton<IDesiredStatePrService, NotYetEnabledDesiredStatePrService>();
        }

        return services;
    }

    private static void RegisterCredentialProvider(IServiceCollection services, GitHubOptions options)
    {
        // Key Vault via managed identity when a vault + secret name are configured (hosted); else the env
        // provider (local/CI/tests). Tests override IGitCredentialProvider with a fake, so use TryAdd.
        if (options.AuthMode == GitPrAuthMode.Pat
            && !string.IsNullOrWhiteSpace(options.KeyVaultUri)
            && !string.IsNullOrWhiteSpace(options.PatSecretName))
        {
            services.AddMemoryCache();
            services.TryAddSingleton(new KeyVaultCredentialSettings(options.KeyVaultUri!, options.PatSecretName!));
            services.TryAddSingleton<IGitCredentialProvider, KeyVaultGitCredentialProvider>();
        }
        else
        {
            services.TryAddSingleton<IGitCredentialProvider, EnvGitCredentialProvider>();
        }
    }

    private static void RegisterGitHubClient(IServiceCollection services, GitHubOptions options)
    {
        services.TryAddSingleton(new GitHubClientSettings(options.ApiBaseUrl, options.RepoOwner, options.RepoName));
        services.AddHttpClient<IGitHubPullRequestClient, GitHubRestPullRequestClient>(http =>
        {
            // Bounded per-request timeout so a stuck GitHub call can never exceed the endpoint's latency budget.
            http.Timeout = TimeSpan.FromSeconds(20);
        });
    }
}
