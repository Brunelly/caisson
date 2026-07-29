using Caisson.Drift;
using Caisson.Infrastructure.Persistence.Drift;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration for the drift computation bridge (story #64). Mirrors
/// <c>PersistenceServiceCollectionExtensions</c>: the caller is expected to have already registered
/// <c>CaissonDbContext</c>. <c>Caisson.Orchestration.DependencyInjection.DriftServiceCollectionExtensions
/// .AddCaissonDrift</c> calls this in turn before adding its own scheduler/signal/pruner hosted services.
/// </summary>
public static class DriftServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IDriftComputationService"/> (scoped) and its bound <see cref="DriftComputationOptions"/>.</summary>
    public static IServiceCollection AddCaissonDriftComputation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DriftComputationOptions>().Bind(configuration.GetSection(DriftComputationOptions.SectionName));
        services.TryAddScoped<IDriftComputationService, DriftComputationService>();

        return services;
    }
}
