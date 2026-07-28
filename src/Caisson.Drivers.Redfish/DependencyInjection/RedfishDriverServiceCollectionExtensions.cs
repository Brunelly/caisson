using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.Redfish.DependencyInjection;

/// <summary>DI registration for the HP iLO / Redfish BMC driver (see docs/adding-a-driver.md).</summary>
public static class RedfishDriverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redfish BMC driver: the default env-backed <see cref="IBmcCredentialResolver"/>,
    /// shared <see cref="RedfishMetrics"/> and the default <see cref="ProcessIpmiCommandRunner"/> (unless
    /// already registered), plus the <see cref="RedfishBmcDriverFactory"/> via <c>AddBmcDriver&lt;T&gt;()</c>
    /// so it is resolved through <c>IBmcDriverRegistry</c> (version-agnostic per ADR 0007). Call
    /// <c>AddCaissonDriverRegistry()</c> as well to build the registry.
    /// </summary>
    public static IServiceCollection AddHpeRedfishBmcDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBmcCredentialResolver, EnvBmcCredentialResolver>();
        services.TryAddSingleton<RedfishMetrics>();
        // Built from ILoggerFactory (the same dependency the driver factory uses) rather than an injected
        // ILogger<T>, so this registration works wherever an ILoggerFactory is present without also
        // requiring the open-generic ILogger<> from AddLogging().
        services.TryAddSingleton<IIpmiCommandRunner>(provider =>
            new ProcessIpmiCommandRunner(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<ProcessIpmiCommandRunner>()));
        services.AddBmcDriver<RedfishBmcDriverFactory>();

        return services;
    }
}
