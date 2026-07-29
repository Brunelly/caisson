using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.Redfish;

/// <summary>
/// The <see cref="IBmcDriverFactory"/> for the HP iLO / Redfish driver. Reports the
/// <c>("HPE", null, Redfish, "1.0.0")</c> descriptor (a generic iLO line; IPMI is an internal fallback, not
/// a separately-resolvable kind) and binds a <c>BmcConnectionOptions</c> — host, port (default 443),
/// timeout and credentials reference — into a driver instance, resolving credentials lazily per connection
/// via the injected <see cref="IBmcCredentialResolver"/>. TLS trust (a certificate fingerprint pin, or an
/// explicit accept-untrusted opt-in) is sourced from environment variables keyed by the credentials
/// reference, mirroring how credentials are resolved — see <c>docs/redfish-discovery.md</c> — so
/// <c>BmcConnectionOptions</c> is not widened.
/// </summary>
public sealed class RedfishBmcDriverFactory : IBmcDriverFactory
{
    /// <summary>The default Redfish HTTPS port.</summary>
    public const int DefaultPort = 443;

    private const string EnvPrefix = "CAISSON_BMC";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IBmcCredentialResolver _credentialResolver;
    private readonly IIpmiCommandRunner _ipmiRunner;
    private readonly RedfishMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHostEnvironment _environment;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates the factory with its injected credential resolver, IPMI runner, metrics, logger factory and host environment.</summary>
    public RedfishBmcDriverFactory(
        IBmcCredentialResolver credentialResolver,
        IIpmiCommandRunner ipmiRunner,
        RedfishMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment)
        : this(credentialResolver, ipmiRunner, metrics, loggerFactory, environment, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: creates the factory with a custom environment lookup for the TLS-trust variables.</summary>
    internal RedfishBmcDriverFactory(
        IBmcCredentialResolver credentialResolver,
        IIpmiCommandRunner ipmiRunner,
        RedfishMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(ipmiRunner);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        _credentialResolver = credentialResolver;
        _ipmiRunner = ipmiRunner;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
        _environment = environment;
        _readEnvironment = readEnvironment;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RedfishBmcDriver.RedfishDescriptor;

    /// <inheritdoc />
    public IBmcDiscoveryDriver Create(BmcConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CredentialReferenceSlug.Validate(options.CredentialsRef, options.Host);

        var port = options.Port ?? DefaultPort;
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : DefaultTimeout;

        // TLS trust is resolved once, up front (it carries no secret): an optional SHA-256 certificate
        // fingerprint pin, or an explicit opt-in to accept an untrusted certificate. AllowUntrustedCertificate
        // is deliberately read from the per-slug variable ONLY — no global fallback — so the blast radius of
        // ever setting it is exactly one device (finding #24).
        var slug = CredentialReferenceSlug.Normalize(options.CredentialsRef);
        var fingerprint = ReadEnv($"{EnvPrefix}_{slug}_TLS_FINGERPRINT", $"{EnvPrefix}_TLS_FINGERPRINT");
        var allowUntrusted = IsTruthy(_readEnvironment($"{EnvPrefix}_{slug}_TLS_ALLOW_UNTRUSTED"));

        if (allowUntrusted)
        {
            if (_environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"{EnvPrefix}_{slug}_TLS_ALLOW_UNTRUSTED is set for device '{options.Host}' under " +
                    "ASPNETCORE_ENVIRONMENT=Production. Disabling TLS certificate validation is only permitted " +
                    "outside Production — refusing to create this BMC driver rather than risk a silent MITM.");
            }

            var logger = _loggerFactory.CreateLogger<RedfishBmcDriverFactory>();
            logger.LogError(
                "TLS certificate validation is explicitly disabled for BMC device {DeviceHost} " +
                "({EnvPrefix}_{Slug}_TLS_ALLOW_UNTRUSTED). This is only safe outside Production.",
                options.Host, EnvPrefix, slug);
        }

        // Credentials are resolved lazily, once per connection, so a missing secret surfaces as a driver
        // error at discovery time rather than a DI-time failure. One credential serves Redfish and IPMI.
        Func<IRedfishClient> redfishClientFactory = () =>
        {
            var credentials = _credentialResolver.Resolve(options.CredentialsRef);
            var settings = new RedfishConnectionSettings(
                options.Host, port, credentials.Username, credentials.Password, timeout, fingerprint, allowUntrusted);
            return new RedfishClient(settings, _loggerFactory.CreateLogger<RedfishClient>());
        };

        Func<IpmiConnectionSettings> ipmiSettingsFactory = () =>
        {
            var credentials = _credentialResolver.Resolve(options.CredentialsRef);
            return new IpmiConnectionSettings(
                options.Host, IpmiConnectionSettings.DefaultPort, credentials.Username, credentials.Password, timeout);
        };

        return new RedfishBmcDriver(
            options.Host, redfishClientFactory, () => _ipmiRunner, ipmiSettingsFactory, timeout, _metrics,
            _loggerFactory.CreateLogger<RedfishBmcDriver>());
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
