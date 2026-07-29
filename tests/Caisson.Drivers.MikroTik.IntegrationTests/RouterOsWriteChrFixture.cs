using Caisson.Drivers.Simulators;
using Xunit;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// Resolves the RouterOS endpoint the write-driver integration suite runs against — mirrors
/// <see cref="RouterOsChrFixture"/>'s <c>CAISSON_CHR_HOST</c>/<c>CAISSON_CHR_API</c> opt-in pattern
/// (ADR 0008/0017) but starts a STATEFUL simulator (<see cref="RouterOsProfile.SwitchState"/>) per test,
/// since the write path's read-modify-verify cycle needs to observe its own writes, unlike the
/// stateless fixture-replay the read-only suite uses.
/// </summary>
public sealed class RouterOsWriteChrFixture : IAsyncLifetime
{
    /// <summary>When set (<c>host</c> or <c>host:port</c>), the suite runs against a real, write-capable CHR at this address.</summary>
    public const string ChrHostEnvVar = "CAISSON_CHR_HOST";

    /// <summary>Alternate opt-in variable (kept alongside <see cref="ChrHostEnvVar"/> for parity with the docs).</summary>
    public const string ChrApiEnvVar = "CAISSON_CHR_API";

    private const string SimulatorUsername = "caisson-write";
    private const string SimulatorPassword = "sim-only-write-password";

    private readonly List<RouterOsApiSimulator> _simulators = new();

    /// <summary>Whether the suite is pointed at a real CHR (true) or the in-process stateful simulator (false).</summary>
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
    /// Starts a fresh stateful simulator seeded with <paramref name="portPvid"/>/<paramref name="vlans"/>
    /// (or, against a real CHR, returns the real endpoint and the seed is ignored — the device's own
    /// state is authoritative). Returns the endpoint plus the simulator instance itself (null against a
    /// real CHR) so a test can drive its observability hooks (<c>GetPortAccessVlan</c>,
    /// <c>HasPendingRollback</c>, <c>AdvanceTime</c>, <c>FireDueRollbacks</c>).
    /// </summary>
    public (RouterOsEndpoint Endpoint, RouterOsApiSimulator? Simulator) StartStatefulSwitch(
        IReadOnlyDictionary<string, int> portPvid,
        IReadOnlyDictionary<int, SimulatorVlanMembership> vlans,
        TimeProvider? timeProvider = null)
    {
        if (UsingRealChr)
        {
            return (new RouterOsEndpoint(_realHost, _realPort), null);
        }

        var profile = new RouterOsProfile { SwitchState = new SimulatorSwitchState(portPvid, vlans) };
        var simulator = new RouterOsApiSimulator(profile, SimulatorUsername, SimulatorPassword, timeProvider: timeProvider);
        simulator.Start();
        _simulators.Add(simulator);
        return (new RouterOsEndpoint(simulator.Host, simulator.Port), simulator);
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
