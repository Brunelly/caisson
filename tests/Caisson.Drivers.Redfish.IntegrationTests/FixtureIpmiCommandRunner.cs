using Caisson.Drivers.Redfish.Transport;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// An <see cref="IIpmiCommandRunner"/> for the integration suite that replays committed <c>ipmitool</c>
/// text fixtures (<c>Fixtures/ipmi-*.txt</c>) wired through the driver's runner seam, so the IPMI fallback
/// path is exercised deterministically without a real BMC. A real <c>ipmitool</c> opt-in against
/// <c>CAISSON_IPMI_HOST</c> is out of scope here; this stub self-supplies the canned output. Unknown
/// subcommands report "unavailable" so a test only sees IPMI data it opted into.
/// </summary>
public sealed class FixtureIpmiCommandRunner : IIpmiCommandRunner
{
    private static readonly IpmiCommandResult Unavailable =
        new(Available: false, ExitCode: -1, string.Empty, string.Empty);

    private readonly Dictionary<string, string> _fixtures;

    public FixtureIpmiCommandRunner(IReadOnlyDictionary<string, string> fixtureFilesBySubcommand)
    {
        _fixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (subcommand, file) in fixtureFilesBySubcommand)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
            _fixtures[subcommand] = File.ReadAllText(path);
        }
    }

    /// <summary>Every subcommand run, in order (joined argv), for fallback assertions.</summary>
    public List<string> Invocations { get; } = new();

    public Task<IpmiCommandResult> RunAsync(
        IReadOnlyList<string> argv, IpmiConnectionSettings settings, CancellationToken cancellationToken)
    {
        // Mirror the production runner's boundary: reject any non-allowlisted subcommand.
        if (!IpmiReadCommands.IsReadOnly(argv))
        {
            throw new InvalidOperationException(
                $"The ipmitool subcommand '{string.Join(' ', argv)}' is not on the read-only allowlist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = string.Join(' ', argv);
        Invocations.Add(key);
        return Task.FromResult(_fixtures.TryGetValue(key, out var output)
            ? new IpmiCommandResult(Available: true, ExitCode: 0, output, string.Empty)
            : Unavailable);
    }
}
