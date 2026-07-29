using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Orchestration.Drift;
using Caisson.Orchestration.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Orchestration.DependencyInjection;

/// <summary>
/// DI registration for drift recompute orchestration (story #64). Wires the pure compute service (via
/// <c>AddCaissonDriftComputation</c>), the real bounded-channel <see cref="IDriftRecomputeSignal"/>
/// (overriding the no-op default the lower layers see standalone), and hosts the scheduler, the
/// event-triggered runner, and the retention pruner. Mirrors
/// <c>OrchestrationServiceCollectionExtensions.AddCaissonOrchestration</c>'s shape.
/// </summary>
public static class DriftServiceCollectionExtensions
{
    /// <summary>Registers everything the drift recompute orchestration layer needs.</summary>
    public static IServiceCollection AddCaissonDrift(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DriftOrchestrationOptions>().Bind(configuration.GetSection(DriftOrchestrationOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        // The compute service + its options (idempotent TryAdd registrations).
        services.AddCaissonDriftComputation(configuration);

        // The real bounded-channel signal wins over the no-op default AddCaissonDriftComputation
        // registered, regardless of registration order (mirrors LiveUpdatesServiceCollectionExtensions).
        services.RemoveAll<IDriftRecomputeSignal>();
        services.AddSingleton<DriftRecomputeSignal>();
        services.AddSingleton<IDriftRecomputeSignal>(sp => sp.GetRequiredService<DriftRecomputeSignal>());

        // Background hosts (each honours its SchedulerEnabled/RetentionEnabled option).
        services.AddHostedService<DriftRecomputeRunner>();
        services.AddHostedService<DriftScheduler>();
        services.AddHostedService<DriftRetentionPruner>();

        return services;
    }
}
