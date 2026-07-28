using Caisson.Infrastructure.Persistence.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration for the story-7 persistence bridge (ADR 0011). Mirrors the driver/correlation DI
/// extensions. The caller is expected to have already registered <c>CaissonDbContext</c> (e.g. via
/// <c>AddDbContext</c>); this adds the ingestion seam and its id generator. The read-query helpers are
/// static extension methods on the context and need no registration.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITopologySnapshotIngestionService"/> (scoped, DbContext-bound) and the
    /// default <see cref="ITopologyIdGenerator"/> (singleton).
    /// </summary>
    /// <param name="services">The service collection to add the persistence services to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddCaissonPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITopologyIdGenerator, GuidTopologyIdGenerator>();
        services.TryAddScoped<ITopologySnapshotIngestionService, TopologySnapshotIngestionService>();

        return services;
    }
}
