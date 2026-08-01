using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Infrastructure.Persistence.Drift;
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

        // Story #308 (ADR 0064): the Tier 1 (mandatory-durable) audit seam TopologySnapshotIngestionService
        // depends on. TryAdd so Caisson.Api's own registration (AddCaissonAuditDurability) — or a test's —
        // wins if it runs first; the stateless default here just keeps every OTHER composition root (e.g.
        // the VirtualRack Seeder) working without needing to know about audit durability specifically.
        services.TryAddSingleton<IMandatoryAuditOutbox, MandatoryAuditOutbox>();

        // Live-updates seam (story #9, ADR 0014): a no-op publisher + in-process sequencer are the
        // fail-open defaults so ingestion/orchestration never hard-depend on Redis. AddCaissonLiveUpdates
        // replaces these with the Redis-backed implementations when a connection string is configured.
        services.TryAddSingleton<ITopologyEventPublisher, NoOpTopologyEventPublisher>();
        services.TryAddSingleton<ITopologyEventSequencer, InProcessTopologyEventSequencer>();

        // Fail-open default (mirrors the ITopologyEventPublisher default above): TopologySnapshotIngestionService
        // depends on IDriftRecomputeSignal, so composition roots that never call AddCaissonDrift (e.g. the
        // VirtualRack Seeder) still get a working service graph. AddCaissonDrift overrides this with the real
        // bounded-channel signal when Orchestration is wired.
        services.TryAddSingleton<IDriftRecomputeSignal, NoOpDriftRecomputeSignal>();

        return services;
    }
}
