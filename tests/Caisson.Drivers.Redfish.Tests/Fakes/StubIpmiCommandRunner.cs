using Caisson.Drivers.Redfish.Transport;

namespace Caisson.Drivers.Redfish.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IIpmiCommandRunner"/> for driver tests: replays a canned
/// <see cref="IpmiCommandResult"/> per subcommand (keyed by its joined argv) and records every invocation
/// so fallback-decision tests can assert whether — and with what — IPMI was invoked. Any unset subcommand
/// defaults to "unavailable" so it contributes no data unless a test opts it in.
/// </summary>
public sealed class StubIpmiCommandRunner : IIpmiCommandRunner
{
    private static readonly IpmiCommandResult Unavailable =
        new(Available: false, ExitCode: -1, string.Empty, string.Empty);

    private readonly Dictionary<string, Func<IpmiCommandResult>> _responses = new(StringComparer.Ordinal);

    /// <summary>Every subcommand run, in order (joined argv, e.g. <c>"mc info"</c>).</summary>
    public List<string> Invocations { get; } = new();

    public void SetOutput(IReadOnlyList<string> argv, string stdout)
        => _responses[Key(argv)] = () => new IpmiCommandResult(Available: true, ExitCode: 0, stdout, string.Empty);

    public void SetResult(IReadOnlyList<string> argv, IpmiCommandResult result)
        => _responses[Key(argv)] = () => result;

    public Task<IpmiCommandResult> RunAsync(
        IReadOnlyList<string> argv, IpmiConnectionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invocations.Add(Key(argv));
        return Task.FromResult(_responses.TryGetValue(Key(argv), out var handler) ? handler() : Unavailable);
    }

    private static string Key(IReadOnlyList<string> argv) => string.Join(' ', argv);
}
