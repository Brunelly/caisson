using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Transport;
using Microsoft.Extensions.Hosting;
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
    /// <summary>
    /// The default RouterOS API port — the TLS transport (8729). TLS is now the fail-closed default:
    /// reaching the legacy plaintext port 8728 requires both an explicit port AND
    /// <see cref="SwitchConnectionOptions.AllowPlaintext"/> (finding #8).
    /// </summary>
    public const int DefaultPort = TlsPort;

    /// <summary>The TLS RouterOS API port.</summary>
    public const int TlsPort = 8729;

    /// <summary>The legacy plaintext RouterOS API port — reachable only via an explicit AllowPlaintext opt-in.</summary>
    public const int PlaintextPort = 8728;

    private const string EnvPrefix = "CAISSON_SWITCH";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ISwitchCredentialResolver _credentialResolver;
    private readonly RouterOsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHostEnvironment _environment;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates the factory with its injected credential resolver, metrics, logger factory and host environment.</summary>
    public RouterOsSwitchDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment)
        : this(credentialResolver, metrics, loggerFactory, environment, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: creates the factory with a custom environment lookup for the TLS-trust variables.</summary>
    internal RouterOsSwitchDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        _credentialResolver = credentialResolver;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
        _environment = environment;
        _readEnvironment = readEnvironment;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RouterOsSwitchDriver.RouterOsDescriptor;

    /// <inheritdoc />
    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CredentialReferenceSlug.Validate(options.CredentialsRef, options.Host);

        // TLS is derived from the explicit UseTls flag, never inferred from the port (finding #8): a TLS
        // API reachable on a non-standard port (NAT, port-forward) must be expressible, and a plaintext
        // connection can never happen by omission.
        var useTls = options.UseTls;
        var port = options.Port ?? (useTls ? TlsPort : PlaintextPort);
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : DefaultTimeout;

        if (!useTls && !options.AllowPlaintext)
        {
            throw new InvalidOperationException(
                $"Switch '{options.Host}' is configured for a plaintext (non-TLS) RouterOS API connection " +
                "but AllowPlaintext was not explicitly set. Set UseTls=true (recommended) or explicitly opt " +
                "into plaintext with AllowPlaintext=true — refusing to send credentials in cleartext by default.");
        }

        // TLS trust is resolved once, up front (it does not carry secrets): an optional SHA-256 certificate
        // fingerprint pin, or an explicit opt-in to accept an untrusted certificate. Only relevant on TLS.
        // AllowUntrustedCertificate is deliberately read from the per-slug variable ONLY — no global
        // fallback — so the blast radius of ever setting it is exactly one device (finding #24).
        var slug = CredentialReferenceSlug.Normalize(options.CredentialsRef);
        var fingerprint = useTls ? ReadEnv($"{EnvPrefix}_{slug}_TLS_FINGERPRINT", $"{EnvPrefix}_TLS_FINGERPRINT") : null;

        if (!useTls && !string.IsNullOrWhiteSpace(ReadEnv($"{EnvPrefix}_{slug}_TLS_FINGERPRINT", $"{EnvPrefix}_TLS_FINGERPRINT")))
        {
            throw new InvalidOperationException(
                $"Switch '{options.Host}' has a {EnvPrefix}_{slug}_TLS_FINGERPRINT configured but UseTls is " +
                "false. A certificate pin on a non-TLS connection is silently meaningless — refusing to start " +
                "rather than let the operator believe the pin is enforced.");
        }

        var allowUntrusted = useTls && IsTruthy(_readEnvironment($"{EnvPrefix}_{slug}_TLS_ALLOW_UNTRUSTED"));

        if (allowUntrusted)
        {
            if (_environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"{EnvPrefix}_{slug}_TLS_ALLOW_UNTRUSTED is set for device '{options.Host}' under " +
                    "ASPNETCORE_ENVIRONMENT=Production. Disabling TLS certificate validation is only permitted " +
                    "outside Production — refusing to create this switch driver rather than risk a silent MITM.");
            }

            var untrustedLogger = _loggerFactory.CreateLogger<RouterOsSwitchDriverFactory>();
            untrustedLogger.LogError(
                "TLS certificate validation is explicitly disabled for switch device {DeviceHost} " +
                "({EnvPrefix}_{Slug}_TLS_ALLOW_UNTRUSTED). This is only safe outside Production.",
                options.Host, EnvPrefix, slug);
        }

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
            options.Host, clientFactory, timeout, _metrics, _loggerFactory.CreateLogger<RouterOsSwitchDriver>());
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
