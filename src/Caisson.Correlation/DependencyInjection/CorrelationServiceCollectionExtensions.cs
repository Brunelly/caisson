using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Correlation.DependencyInjection;

/// <summary>DI registration for the topology correlation engine (story #6, see ADR 0010).</summary>
public static class CorrelationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITopologyCorrelationEngine"/> as a singleton. The engine is stateless and
    /// pure, so a single shared instance is safe and allocation-free per call.
    /// </summary>
    /// <param name="services">The service collection to add the engine to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTopologyCorrelation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITopologyCorrelationEngine, TopologyCorrelationEngine>();

        return services;
    }
}
