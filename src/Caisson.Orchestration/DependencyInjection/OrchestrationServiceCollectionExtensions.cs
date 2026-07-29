using Caisson.Correlation.DependencyInjection;
using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.MikroTik.DependencyInjection;
using Caisson.Drivers.Redfish.DependencyInjection;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Caisson.Orchestration.Runner;
using Caisson.Orchestration.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Orchestration.DependencyInjection;

/// <summary>
/// DI registration for discovery orchestration (story #8, ADR 0013). Wires the read-only drivers +
/// registries, the correlation engine, the persistence bridge, the config-bound rack definitions, the
/// device/orchestrator/job services, and hosts the runner + scheduler background services. The caller is
/// expected to have already registered <c>CaissonDbContext</c> (via <c>AddCaissonPersistence</c>'s host).
/// </summary>
public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>Registers everything the discovery orchestration layer needs.</summary>
    public static IServiceCollection AddCaissonOrchestration(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RackDefinitionOptions>()
            .Bind(configuration.GetSection(RackDefinitionOptions.SectionName));
        services.AddOptions<DiscoveryOrchestrationOptions>()
            .Bind(configuration.GetSection(DiscoveryOrchestrationOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        // Read-only drivers + registry (Orchestration is the one layer allowed to touch Caisson.Drivers.*).
        services.AddMikroTikRouterOsSwitchDriver();
        services.AddHpeRedfishBmcDriver();
        services.AddCaissonDriverRegistry();

        // Correlation engine + persistence bridge (idempotent TryAdd registrations).
        services.AddTopologyCorrelation();
        services.AddCaissonPersistence();

        // Coordination singletons.
        services.TryAddSingleton<DiscoveryJobSignal>();
        services.TryAddSingleton<DiscoveryCancellationRegistry>();
        services.TryAddSingleton<IJitterSource, RandomJitterSource>();

        // Scoped per-run services.
        services.TryAddScoped<IRackDefinitionProvider, ConfigurationRackDefinitionProvider>();
        services.TryAddScoped<IDeviceDiscoveryService, DeviceDiscoveryService>();
        services.TryAddScoped<IDiscoveryJobStore, CaissonDiscoveryJobStore>();
        services.TryAddScoped<IDiscoveryOrchestrator, DiscoveryOrchestrator>();
        services.TryAddScoped<IDiscoveryJobService, DiscoveryJobService>();

        // Background hosts (each honours its RunnerEnabled/SchedulerEnabled option).
        services.AddHostedService<DiscoveryJobRunner>();
        services.AddHostedService<DiscoveryScheduler>();

        return services;
    }

    /// <summary>
    /// Registers the single-change drift-apply orchestration layer (story #65): the write-capable RouterOS
    /// driver, the job/query service, the revalidation+device-apply orchestrator, and the background
    /// runner. Assumes <see cref="AddCaissonOrchestration"/> has already been called on the same
    /// <paramref name="services"/> — it builds the shared driver registries
    /// (<c>AddCaissonDriverRegistry()</c>) that this method's write-capable driver factory registers into.
    /// Keeps <c>Caisson.Api</c> driver-assembly-free: it references only this project, never
    /// <c>Caisson.Drivers.*</c> directly (the <c>Api_references_no_driver_assembly</c> guard).
    /// </summary>
    public static IServiceCollection AddCaissonDriftApply(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DriftApplyOrchestrationOptions>()
            .Bind(configuration.GetSection(DriftApplyOrchestrationOptions.SectionName));

        // Write-capable driver (Orchestration is the one layer allowed to touch Caisson.Drivers.*).
        services.AddMikroTikRouterOsSwitchMutatingDriver();

        services.TryAddSingleton<DriftApplyJobSignal>();

        services.TryAddScoped<IDriftApplyJobService, DriftApplyJobService>();
        services.TryAddScoped<IDriftApplyOrchestrator, DriftApplyOrchestrator>();

        services.AddHostedService<DriftApplyJobRunner>();

        return services;
    }
}
