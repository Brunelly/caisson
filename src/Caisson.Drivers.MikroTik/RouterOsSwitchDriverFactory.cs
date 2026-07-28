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
/// injected <see cref="ISwitchCredentialResolver"/>. TLS trust for the 8729 transport (a certificate
/// fingerprint pin, or an explicit accept-untrusted opt-in) is sourced from environment variables
/// keyed by the credentials reference, mirroring how credentials are resolved — see
/// <c>docs/routeros-discovery.md</c>.
/// </summary>
public sealed class RouterOsSwitchDriverFactory : ISwitchDriverFactory
{
    /// <summary>The default RouterOS API port (plaintext); 8729 selects the TLS transport.</summary>
    public const int DefaultPort = 8728;

    /// <summary>The TLS RouterOS API port.</summary>
    public const int TlsPort = 8729;

    private const string EnvPrefix = "CAISSON_SWITCH";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ISwitchCredentialResolver _credentialResolver;
    private readonly RouterOsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates the factory with its injected credential resolver, metrics and logger factory.</summary>
    public RouterOsSwitchDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsMetrics metrics,
        ILoggerFactory loggerFactory)
        : this(credentialResolver, metrics, loggerFactory, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: creates the factory with a custom environment lookup for the TLS-trust variables.</summary>
    internal RouterOsSwitchDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsMetrics metrics,
        ILoggerFactory loggerFactory,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        _credentialResolver = credentialResolver;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
        _readEnvironment = readEnvironment;
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

        // TLS trust is resolved once, up front (it does not carry secrets): an optional SHA-256 certificate
        // fingerprint pin, or an explicit opt-in to accept an untrusted certificate. Only relevant on TLS.
        var slug = CredentialReferenceSlug.Normalize(options.CredentialsRef);
        var fingerprint = useTls ? ReadEnv($"{EnvPrefix}_{slug}_TLS_FINGERPRINT", $"{EnvPrefix}_TLS_FINGERPRINT") : null;
        var allowUntrusted = useTls && IsTruthy(ReadEnv($"{EnvPrefix}_{slug}_TLS_ALLOW_UNTRUSTED", $"{EnvPrefix}_TLS_ALLOW_UNTRUSTED"));

        // Credentials are resolved lazily, once per connection, so a missing secret surfaces as a
        // driver error at discovery time rather than a DI-time failure.
        Func<IRouterOsApiClient> clientFactory = () =>
        {
            var credentials = _credentialResolver.Resolve(options.CredentialsRef);
            var settings = new RouterOsConnectionSettings(
                options.Host, port, useTls, credentials.Username, credentials.Password, timeout,
                fingerprint, allowUntrusted);
            return new RouterOsApiClient(settings, _loggerFactory.CreateLogger<RouterOsApiClient>());
        };

        return new RouterOsSwitchDriver(
            options.Host, clientFactory, _metrics, _loggerFactory.CreateLogger<RouterOsSwitchDriver>());
    }

    private string? ReadEnv(string primary, string fallback)
    {
        var value = _readEnvironment(primary);
        if (string.IsNullOrEmpty(value))
        {
            value = _readEnvironment(fallback);
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool IsTruthy(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
