using Xunit;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// Resolves the RouterOS endpoint the integration suite runs against. It <b>prefers</b> a real CHR when
/// <see cref="ChrHostEnvVar"/>/<see cref="ChrApiEnvVar"/> is set (opt-in, e.g. against real hardware),
/// and <b>falls back</b> to the in-process <see cref="RouterOsApiSimulator"/> otherwise — mirroring the
/// <c>CAISSON_TEST_DB</c> pattern in <c>PostgresFixture</c> so the suite is green in hardware-free CI and
/// against real CHR with no code change.
/// </summary>
public sealed class RouterOsChrFixture : IAsyncLifetime
{
    /// <summary>When set (<c>host</c> or <c>host:port</c>), the suite runs against a real CHR at this address.</summary>
    public const string ChrHostEnvVar = "CAISSON_CHR_HOST";

    /// <summary>Alternate opt-in variable (kept alongside <see cref="ChrHostEnvVar"/> for parity with the docs).</summary>
    public const string ChrApiEnvVar = "CAISSON_CHR_API";

    private const string SimulatorUsername = "caisson-ro";
    private const string SimulatorPassword = "sim-only-password";

    private readonly List<RouterOsApiSimulator> _simulators = new();

    /// <summary>Whether the suite is pointed at a real CHR (true) or the in-process simulator (false).</summary>
    public bool UsingRealChr { get; private set; }

    /// <summary>
    /// The environment lookup the credential resolver uses: the real process environment (CI secrets)
    /// against real CHR, or fixed simulator credentials otherwise.
    /// </summary>
    public Func<string, string?> EnvLookup { get; private set; } = _ => null;

    private string _realHost = string.Empty;
    private int _realPort = 8728;

    public Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ChrHostEnvVar)
            ?? Environment.GetEnvironmentVariable(ChrApiEnvVar);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            UsingRealChr = true;
            var parts = configured.Split(':', 2);
            _realHost = parts[0];
            _realPort = parts.Length > 1 && int.TryParse(parts[1], out var port) ? port : 8728;
            EnvLookup = Environment.GetEnvironmentVariable;
        }
        else
        {
            UsingRealChr = false;
            EnvLookup = SimulatorEnvLookup;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the endpoint for a given fixture profile. Against real CHR the profile is ignored and the
    /// real endpoint is returned; against the simulator a fresh simulator is started for the profile.
    /// </summary>
    public RouterOsEndpoint ResolveEndpoint(string profileName)
    {
        if (UsingRealChr)
        {
            return new RouterOsEndpoint(_realHost, _realPort);
        }

        var simulator = new RouterOsApiSimulator(
            RouterOsApiSimulator.LoadProfile(profileName), SimulatorUsername, SimulatorPassword);
        simulator.Start();
        _simulators.Add(simulator);
        return new RouterOsEndpoint(simulator.Host, simulator.Port);
    }

    public async Task DisposeAsync()
    {
        foreach (var simulator in _simulators)
        {
            await simulator.DisposeAsync();
        }
    }

    private static string? SimulatorEnvLookup(string name) => name switch
    {
        "CAISSON_SWITCH_USERNAME" => SimulatorUsername,
        "CAISSON_SWITCH_PASSWORD" => SimulatorPassword,
        _ => null,
    };
}

/// <summary>A resolved RouterOS endpoint (host + port).</summary>
public sealed record RouterOsEndpoint(string Host, int Port);
