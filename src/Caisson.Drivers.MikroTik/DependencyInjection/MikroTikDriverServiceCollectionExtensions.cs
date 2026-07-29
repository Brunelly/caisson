using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Drivers.MikroTik.DependencyInjection;

/// <summary>DI registration for the MikroTik RouterOS switch driver (see docs/adding-a-driver.md).</summary>
public static class MikroTikDriverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RouterOS switch driver: the default env-backed <see cref="ISwitchCredentialResolver"/>
    /// and shared <see cref="RouterOsMetrics"/> (unless already registered), plus the
    /// <see cref="RouterOsSwitchDriverFactory"/> via <c>AddSwitchDriver&lt;T&gt;()</c> so it is resolved
    /// through <c>ISwitchDriverRegistry</c>. Call <c>AddCaissonDriverRegistry()</c> as well to build the
    /// registry.
    /// </summary>
    public static IServiceCollection AddMikroTikRouterOsSwitchDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISwitchCredentialResolver, EnvSwitchCredentialResolver>();
        services.TryAddSingleton<RouterOsMetrics>();
        services.AddSwitchDriver<RouterOsSwitchDriverFactory>();

        return services;
    }

    /// <summary>
    /// Registers the RouterOS write driver (ADR 0031): the shared env-backed
    /// <see cref="ISwitchCredentialResolver"/> and system <see cref="TimeProvider"/> (unless already
    /// registered), the dedicated <see cref="RouterOsWriteMetrics"/>, plus the
    /// <see cref="RouterOsSwitchMutatingDriverFactory"/> via <c>AddSwitchMutatingDriver&lt;T&gt;()</c> so
    /// it is resolved through <c>ISwitchMutatingDriverRegistry</c> — a separate extension from
    /// <see cref="AddMikroTikRouterOsSwitchDriver"/> so registering write capability is a distinct,
    /// explicit action. Call <c>AddCaissonDriverRegistry()</c> as well to build the registries.
    /// </summary>
    public static IServiceCollection AddMikroTikRouterOsSwitchMutatingDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISwitchCredentialResolver, EnvSwitchCredentialResolver>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RouterOsWriteMetrics>();
        services.AddSwitchMutatingDriver<RouterOsSwitchMutatingDriverFactory>();

        return services;
    }
}
