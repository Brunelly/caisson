using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik;

/// <summary>
/// The <see cref="ISwitchMutatingDriverFactory"/> for the MikroTik RouterOS write driver. Mirrors
/// <see cref="RouterOsSwitchDriverFactory"/>'s TLS/cert-pin resolution, fail-closed plaintext/production
/// gates and env-based credential resolution exactly (ADR 0019/0020) — the write path gets the same
/// transport security posture as discovery, plus a confirmed-commit window default (30s, configurable
/// per environment via <see cref="SwitchMutatingConnectionOptions.ConfirmWindow"/>).
/// </summary>
public sealed class RouterOsSwitchMutatingDriverFactory : ISwitchMutatingDriverFactory
{
    /// <summary>The default RouterOS API port — the TLS transport (8729), matching the read driver's fail-closed default.</summary>
    public const int DefaultPort = TlsPort;

    /// <summary>The TLS RouterOS API port.</summary>
    public const int TlsPort = 8729;

    /// <summary>The legacy plaintext RouterOS API port — reachable only via an explicit AllowPlaintext opt-in.</summary>
    public const int PlaintextPort = 8728;

    private const string EnvPrefix = "CAISSON_SWITCH";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The overall per-call driver budget. Larger than the read driver's (which reuses its per-command
    /// timeout) because a single SetAccessVlanAsync apply involves more sequential round trips (read
    /// port, read VLANs, arm scheduler, set, verify) than any single discovery query.
    /// </summary>
    private static readonly TimeSpan DefaultOperationBudget = TimeSpan.FromSeconds(8);

    private readonly ISwitchCredentialResolver _credentialResolver;
    private readonly RouterOsWriteMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates the factory with its injected credential resolver, metrics, logger factory, host environment and time provider.</summary>
    public RouterOsSwitchMutatingDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsWriteMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment,
        TimeProvider timeProvider)
        : this(credentialResolver, metrics, loggerFactory, environment, timeProvider, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: creates the factory with a custom environment lookup for the TLS-trust variables.</summary>
    internal RouterOsSwitchMutatingDriverFactory(
        ISwitchCredentialResolver credentialResolver,
        RouterOsWriteMetrics metrics,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        _credentialResolver = credentialResolver;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
        _environment = environment;
        _timeProvider = timeProvider;
        _readEnvironment = readEnvironment;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RouterOsSwitchMutatingDriver.RouterOsMutatingDescriptor;

    /// <inheritdoc />
    public ISwitchMutatingDriver Create(SwitchMutatingConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CredentialReferenceSlug.Validate(options.CredentialsRef, options.Host);

        // TLS is derived from the explicit UseTls flag, never inferred from the port — same fail-closed
        // rule as the read driver (ADR 0020).
        var useTls = options.UseTls;
        var port = options.Port ?? (useTls ? TlsPort : PlaintextPort);
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : DefaultTimeout;
        var confirmWindow = options.ConfirmWindow is { } w && w > TimeSpan.Zero
            ? w
            : SwitchMutatingConnectionOptions.DefaultConfirmWindow;

        if (!useTls && !options.AllowPlaintext)
        {
            throw new InvalidOperationException(
                $"Switch '{options.Host}' is configured for a plaintext (non-TLS) RouterOS API connection " +
                "but AllowPlaintext was not explicitly set. Set UseTls=true (recommended) or explicitly opt " +
                "into plaintext with AllowPlaintext=true — refusing to send credentials in cleartext by default.");
        }

        // TLS trust is resolved once, up front: an optional SHA-256 certificate fingerprint pin, or an
        // explicit opt-in to accept an untrusted certificate. Only relevant on TLS. AllowUntrustedCertificate
        // is per-slug only — no global fallback — so the blast radius of ever setting it is one device.
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
                    "outside Production — refusing to create this switch mutating driver rather than risk a silent MITM.");
            }

            var untrustedLogger = _loggerFactory.CreateLogger<RouterOsSwitchMutatingDriverFactory>();
            untrustedLogger.LogError(
                "TLS certificate validation is explicitly disabled for switch write device {DeviceHost} " +
                "({EnvPrefix}_{Slug}_TLS_ALLOW_UNTRUSTED). This is only safe outside Production.",
                options.Host, EnvPrefix, slug);
        }

        // Credentials are resolved lazily, once per connection, so a missing secret surfaces as a
        // driver error at call time rather than a DI-time failure. A write-capable RouterOS user needs
        // a more privileged policy than the read-only discovery user (docs/routeros-write.md) — that is
        // an operational credential-provisioning concern, not a code-shape one.
        Func<IRouterOsWriteApiClient> clientFactory = () =>
        {
            var credentials = _credentialResolver.Resolve(options.CredentialsRef);
            var settings = new RouterOsConnectionSettings(
                options.Host, port, useTls, credentials.Username, credentials.Password, timeout,
                fingerprint, allowUntrusted);
            return new RouterOsWriteApiClient(settings, _loggerFactory.CreateLogger<RouterOsWriteApiClient>());
        };

        return new RouterOsSwitchMutatingDriver(
            options.Host, clientFactory, DefaultOperationBudget, confirmWindow, _metrics, _timeProvider,
            _loggerFactory.CreateLogger<RouterOsSwitchMutatingDriver>());
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
