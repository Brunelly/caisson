using Caisson.Drivers.MikroTik.Transport;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// An in-memory <see cref="IRouterOsApiClient"/> for driver tests: per-command canned responses (or
/// throwing delegates to simulate traps/timeouts) and an optional connect hook for auth failures.
/// </summary>
public sealed class FakeRouterOsApiClient : IRouterOsApiClient
{
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, string>> Empty =
        Array.Empty<IReadOnlyDictionary<string, string>>();

    /// <summary>Per-command handlers. A handler may return rows or throw to simulate a failure.</summary>
    public Dictionary<string, Func<IReadOnlyList<IReadOnlyDictionary<string, string>>>> Responses { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Optional hook run by <see cref="ConnectAsync"/> — throw here to simulate an auth/connect failure.</summary>
    public Func<Task>? OnConnect { get; set; }

    public int DisposeCount { get; private set; }

    public void SetRows(string command, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        => Responses[command] = () => rows;

    public void SetThrows(string command, Func<Exception> exceptionFactory)
        => Responses[command] = () => throw exceptionFactory();

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OnConnect?.Invoke() ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> SendCommandAsync(
        string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Responses.TryGetValue(command, out var handler)
            ? Task.FromResult(handler())
            : Task.FromResult(Empty);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
