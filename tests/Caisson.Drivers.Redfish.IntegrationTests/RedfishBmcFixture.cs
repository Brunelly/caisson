using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// Resolves the Redfish endpoint the integration suite runs against. It <b>prefers</b> a real iLO when
/// <see cref="IloHostEnvVar"/> is set (opt-in, e.g. against real hardware) and <b>falls back</b> to the
/// in-process <see cref="RedfishSimulator"/> otherwise — mirroring <c>RouterOsChrFixture</c> so the suite
/// is green in hardware-free CI and against real hardware with no code change. CI TLS trust is configured
/// via the SHA-256 pin of the simulator's generated certificate (a production-safe posture); one test
/// exercises the explicit allow-untrusted opt-in, which is scoped to integration tests only.
/// </summary>
public sealed class RedfishBmcFixture : IAsyncLifetime
{
    /// <summary>When set (<c>host</c> or <c>host:port</c>), the suite runs against a real iLO at this address.</summary>
    public const string IloHostEnvVar = "CAISSON_ILO_HOST";

    /// <summary>When set, an optional real-ipmitool opt-in for the IPMI fallback tests (self-skips otherwise).</summary>
    public const string IpmiHostEnvVar = "CAISSON_IPMI_HOST";

    /// <summary>The credentials reference the tests bind; a fixed slug for the simulator's env lookup.</summary>
    public const string CredentialsRef = "ilo-node";

    private const string SimulatorUsername = "ilo-ro";
    private const string SimulatorPassword = "sim-only-password";

    private readonly List<RedfishSimulator> _simulators = new();
    private X509Certificate2? _certificate;
    private string _fingerprint = string.Empty;

    private string _realHost = string.Empty;
    private int _realPort = RedfishBmcFixtureDefaults.HttpsPort;

    /// <summary>Whether the suite is pointed at real iLO hardware (true) or the in-process simulator (false).</summary>
    public bool UsingRealHardware { get; private set; }

    public Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(IloHostEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            UsingRealHardware = true;
            var parts = configured.Split(':', 2);
            _realHost = parts[0];
            _realPort = parts.Length > 1 && int.TryParse(parts[1], out var port)
                ? port
                : RedfishBmcFixtureDefaults.HttpsPort;
        }
        else
        {
            UsingRealHardware = false;
            _certificate = GenerateCertificate();
            _fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.GetRawCertData()));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The env lookup the credential resolver and driver factory use: real process environment (CI secrets)
    /// against real hardware, or fixed simulator credentials plus the simulator's certificate pin otherwise —
    /// the production-safe posture (validated TLS, not blanket-accept).
    /// </summary>
    public Func<string, string?> PinnedEnvLookup => UsingRealHardware
        ? Environment.GetEnvironmentVariable
        : name => name switch
        {
            "CAISSON_BMC_USERNAME" => SimulatorUsername,
            "CAISSON_BMC_PASSWORD" => SimulatorPassword,
            "CAISSON_BMC_TLS_FINGERPRINT" => _fingerprint,
            _ => null,
        };

    /// <summary>
    /// An env lookup that opts in to accepting the untrusted simulator certificate instead of pinning it —
    /// the answered-question override, scoped to integration tests and disallowed in production config.
    /// </summary>
    public Func<string, string?> AllowUntrustedEnvLookup => name => name switch
    {
        "CAISSON_BMC_USERNAME" => SimulatorUsername,
        "CAISSON_BMC_PASSWORD" => SimulatorPassword,
        "CAISSON_BMC_TLS_ALLOW_UNTRUSTED" => "true",
        _ => null,
    };

    /// <summary>The generated simulator certificate (self-signed), for tests that pin or reject it directly.</summary>
    public X509Certificate2 Certificate =>
        _certificate ?? throw new InvalidOperationException("No simulator certificate when running against real hardware.");

    /// <summary>The SHA-256 fingerprint of the simulator certificate.</summary>
    public string Fingerprint => _fingerprint;

    /// <summary>
    /// Returns the endpoint for a fixture profile. Against real hardware the profile is ignored and the real
    /// endpoint is returned; against the simulator a fresh simulator is started for the profile.
    /// </summary>
    public RedfishEndpoint ResolveEndpoint(string profileName)
    {
        if (UsingRealHardware)
        {
            return new RedfishEndpoint(_realHost, _realPort);
        }

        var simulator = ResolveSimulator(profileName);
        return new RedfishEndpoint(simulator.Host, simulator.Port);
    }

    /// <summary>
    /// Starts a fresh simulator for <paramref name="profileName"/> and returns it directly, so a test can both
    /// drive the driver against it and inspect <see cref="RedfishSimulator.RequestedPaths"/>. Simulator-only —
    /// callers must self-skip under <see cref="UsingRealHardware"/>.
    /// </summary>
    public RedfishSimulator ResolveSimulator(string profileName)
    {
        var simulator = new RedfishSimulator(RedfishSimulator.LoadProfile(profileName), Certificate);
        simulator.Start();
        _simulators.Add(simulator);
        return simulator;
    }

    /// <summary>An endpoint on a closed loopback port — models an unreachable BMC (connection refused).</summary>
    public RedfishEndpoint UnreachableEndpoint()
    {
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new RedfishEndpoint(System.Net.IPAddress.Loopback.ToString(), port);
    }

    public async Task DisposeAsync()
    {
        foreach (var simulator in _simulators)
        {
            await simulator.DisposeAsync();
        }

        _certificate?.Dispose();
    }

    private static X509Certificate2 GenerateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ilo.sim.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning disable SYSLIB0057 // net8 has no X509CertificateLoader; the constructor is the supported path here.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
    }
}

internal static class RedfishBmcFixtureDefaults
{
    public const int HttpsPort = 443;
}

/// <summary>A resolved Redfish endpoint (host + port).</summary>
public sealed record RedfishEndpoint(string Host, int Port);
