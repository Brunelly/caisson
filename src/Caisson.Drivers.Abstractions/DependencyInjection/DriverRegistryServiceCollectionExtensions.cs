using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace Caisson.Drivers.Abstractions.DependencyInjection;

/// <summary>
/// DI registration for the driver registries and their factories. This is the extension point future
/// vendor driver stories (#4/#5) call — this project does not change when a new driver is added.
/// </summary>
public static class DriverRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISwitchDriverRegistry"/> and <see cref="IBmcDriverRegistry"/> as
    /// singletons, built from whichever <see cref="ISwitchDriverFactory"/>/<see cref="IBmcDriverFactory"/>
    /// instances are already registered in <paramref name="services"/> (see
    /// <see cref="AddSwitchDriver{TFactory}"/>/<see cref="AddBmcDriver{TFactory}"/>).
    /// </summary>
    public static IServiceCollection AddCaissonDriverRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ISwitchDriverRegistry>(
            provider => new SwitchDriverRegistry(provider.GetServices<ISwitchDriverFactory>()));
        services.AddSingleton<IBmcDriverRegistry>(
            provider => new BmcDriverRegistry(provider.GetServices<IBmcDriverFactory>()));
        services.AddSingleton<ISwitchMutatingDriverRegistry>(
            provider => new SwitchMutatingDriverRegistry(provider.GetServices<ISwitchMutatingDriverFactory>()));

        return services;
    }

    /// <summary>Registers <typeparamref name="TFactory"/> as an <see cref="ISwitchDriverFactory"/>.</summary>
    public static IServiceCollection AddSwitchDriver<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        this IServiceCollection services)
        where TFactory : class, ISwitchDriverFactory
    {
        services.AddSingleton<ISwitchDriverFactory, TFactory>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TFactory"/> as an <see cref="ISwitchMutatingDriverFactory"/>. A
    /// separate extension from <see cref="AddSwitchDriver{TFactory}"/> so registering a write-capable
    /// driver is a distinct, explicit action from registering the read-only one (AC1).
    /// </summary>
    public static IServiceCollection AddSwitchMutatingDriver<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        this IServiceCollection services)
        where TFactory : class, ISwitchMutatingDriverFactory
    {
        services.AddSingleton<ISwitchMutatingDriverFactory, TFactory>();
        return services;
    }

    /// <summary>Registers <typeparamref name="TFactory"/> as an <see cref="IBmcDriverFactory"/>.</summary>
    public static IServiceCollection AddBmcDriver<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        this IServiceCollection services)
        where TFactory : class, IBmcDriverFactory
    {
        services.AddSingleton<IBmcDriverFactory, TFactory>();
        return services;
    }
}
