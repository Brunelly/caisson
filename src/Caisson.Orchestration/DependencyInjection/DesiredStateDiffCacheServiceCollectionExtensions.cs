using Caisson.Infrastructure.DependencyInjection;
using Caisson.Orchestration.Drift;
using Caisson.Orchestration.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Orchestration.DependencyInjection;

/// <summary>
/// DI registration for the impact-preview diff cache TTL pruner (story #171, Task #197). Binds the
/// <c>DesiredState:DiffCache</c> options and hosts <see cref="DesiredStateDiffCachePruner"/> (which honours
/// its <see cref="DesiredStateDiffCacheOptions.Enabled"/> gate). Assumes <c>CaissonDbContext</c> is already
/// registered (via <c>AddCaissonPersistence</c>).
/// </summary>
public static class DesiredStateDiffCacheServiceCollectionExtensions
{
    /// <summary>Registers the diff cache options and its background TTL pruner.</summary>
    public static IServiceCollection AddCaissonDesiredStateDiffCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DesiredStateDiffCacheOptions>()
            .Bind(configuration.GetSection(DesiredStateDiffCacheOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddCaissonPersistence();

        services.AddHostedService<DesiredStateDiffCachePruner>();

        return services;
    }
}
