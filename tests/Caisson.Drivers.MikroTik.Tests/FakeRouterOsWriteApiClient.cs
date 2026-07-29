using Caisson.Drivers.MikroTik.Transport;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// An in-memory <see cref="IRouterOsWriteApiClient"/> for mutating-driver tests — mirrors
/// <see cref="FakeRouterOsApiClient"/> but keyed to the write client's <c>ExecuteAsync(command, words,
/// ct)</c> shape: per-command handlers (given the sentence's attribute/query words, so a test can react
/// to e.g. a specific <c>pvid</c> being set), canned rows, throwing delegates to simulate traps/timeouts,
/// and a full call log so tests can assert exact command ordering (AC3's arm→apply→verify→confirm order).
/// </summary>
public sealed class FakeRouterOsWriteApiClient : IRouterOsWriteApiClient
{
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, string>> Empty =
        Array.Empty<IReadOnlyDictionary<string, string>>();

    /// <summary>Per-command handlers. A handler may return rows (given the words sent) or throw to simulate a failure.</summary>
    public Dictionary<string, Func<IReadOnlyList<string>, IReadOnlyList<IReadOnlyDictionary<string, string>>>> Responses { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Every command sent, in order, with its attribute/query words — for asserting exact call ordering.</summary>
    public List<(string Command, IReadOnlyList<string> Words)> Calls { get; } = new();

    /// <summary>Optional hook run by <see cref="ConnectAsync"/> — throw here to simulate an auth/connect failure.</summary>
    public Func<Task>? OnConnect { get; set; }

    /// <summary>How many times <see cref="ConnectAsync"/> was called — lets a test assert zero I/O for a pre-validation rejection.</summary>
    public int ConnectCount { get; private set; }

    /// <summary>How many times <see cref="DisposeAsync"/> was called.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Every command path sent, in order — a convenience projection of <see cref="Calls"/>.</summary>
    public IReadOnlyList<string> SentCommands => Calls.Select(c => c.Command).ToArray();

    public void SetRows(string command, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        => Responses[command] = _ => rows;

    public void SetHandler(
        string command, Func<IReadOnlyList<string>, IReadOnlyList<IReadOnlyDictionary<string, string>>> handler)
        => Responses[command] = handler;

    public void SetThrows(string command, Func<Exception> exceptionFactory)
        => Responses[command] = _ => throw exceptionFactory();

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        return OnConnect?.Invoke() ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ExecuteAsync(
        string command, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((command, words));
        return Responses.TryGetValue(command, out var handler)
            ? Task.FromResult(handler(words))
            : Task.FromResult(Empty);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
