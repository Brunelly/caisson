using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Transport;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik;

/// <summary>
/// The <see cref="ISwitchDriverFactory"/> for the MikroTik RouterOS driver. Reports the
/// <c>("MikroTik", null, RouterOsApi, "1.0.0")</c> descriptor and binds a
/// <c>SwitchConnectionOptions</c> — host, port (default 8728; 8729 selects TLS), timeout and
/// credentials reference — into a driver instance, resolving credentials lazily per connection via the
/// injected <see cref="ISwitchCredentialResolver"/>.
/// </summary>
public sealed class RouterOsSwitchDriverFactory : ISwitchDriverFactory
{
    /// <summary>The default RouterOS API port (plaintext); 8729 selects the TLS transport.</summary>
    public const int DefaultPort = 8728;

    /// <summary>The TLS RouterOS API port.</summary>
    public const int TlsPort = 8729;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ISwitchCredentialResolver _credentialResolver;
    private readonly RouterOsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the factory with its injected credential resolver, metrics and logger factory.</summary>
    public RouterOsSwitchDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _credentialResolver = credentialResolver;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RouterOsSwitchDriver.RouterOsDescriptor;

    /// <inheritdoc />
    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var port = options.Port ?? DefaultPort;
        var useTls = port == TlsPort;
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : DefaultTimeout;

        // Credentials are resolved lazily, once per connection, so a missing secret surfaces as a
        // driver error at discovery time rather than a DI-time failure.
        Func<IRouterOsApiClient> clientFactory = () =>
        {
            var credentials = _credentialResolver.Resolve(options.CredentialsRef);
            var settings = new RouterOsConnectionSettings(
                options.Host, port, useTls, credentials.Username, credentials.Password, timeout);
            return new RouterOsApiClient(settings, _loggerFactory.CreateLogger<RouterOsApiClient>());
        };

        return new RouterOsSwitchDriver(
            options.Host, clientFactory, _metrics, _loggerFactory.CreateLogger<RouterOsSwitchDriver>());
    }
}
