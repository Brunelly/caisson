using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Ingestion.Git;
using Caisson.Ingestion.Git.ReadOnly;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Caisson.Ingestion.Runner;
using Caisson.Ingestion.Scheduling;
using Caisson.Ingestion.Security;
using Caisson.Ingestion.Webhook;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Ingestion.DependencyInjection;

/// <summary>
/// DI registration for Git-backed desired-state ingestion (story #62). Mirrors
/// <c>Caisson.Orchestration.DependencyInjection.OrchestrationServiceCollectionExtensions</c>: bind
/// options, register the coordination singletons and per-run services, add the hosted background
/// services. Caller is expected to have already registered <c>CaissonDbContext</c> (e.g. via
/// <c>AddDbContext</c>) — same contract as <c>AddCaissonPersistence</c>.
/// </summary>
public static class GitIngestionServiceCollectionExtensions
{
    public static IServiceCollection AddCaissonGitIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GitIngestionOptions>()
            .Bind(configuration.GetSection(GitIngestionOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddCaissonPersistence();

        services.TryAddSingleton<DesiredStateIngestionSignal>();
        services.TryAddSingleton<IGitIngestionSecretsResolver, EnvGitIngestionSecretsResolver>();
        services.TryAddSingleton<IWebhookSignatureVerifier, GitHubHmacSignatureVerifier>();
        services.TryAddSingleton<GitIngestionMetrics>();

        // Singleton: the bare mirror and its serializing gate are keyed to the one configured
        // repo/branch for the app's lifetime, exactly like the config-bound rack definitions.
        services.TryAddSingleton<IGitRepositoryProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GitIngestionOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LibGit2SharpRepositoryProvider>>();
            return new LibGit2SharpRepositoryProvider(options.RepoUrl, options.LocalMirrorPath, logger);
        });

        services.TryAddScoped<IDesiredStateIngestionService, DesiredStateIngestionService>();

        // Fail-open default (mirrors AddCaissonPersistence's own IDriftRecomputeSignal default):
        // DesiredStateIngestionService depends on IDriftRecomputeSignal, so this holds even for composition
        // roots that call AddCaissonGitIngestion without also wiring Orchestration's AddCaissonDrift.
        services.TryAddSingleton<IDriftRecomputeSignal, NoOpDriftRecomputeSignal>();

        services.AddHostedService<GitPollingBackgroundService>();
        services.AddHostedService<DesiredStateIngestionRunner>();

        return services;
    }
}
