using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// A minimal <see cref="ILogger{T}"/> that captures every formatted log message and scope state so
/// redaction tests can scan the full output for secret material. It is also an <see cref="ILogger"/>,
/// so it can be passed straight to <c>RouterOsApiClient</c>.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<string> _scopes = new();

    /// <summary>All formatted messages emitted so far.</summary>
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    /// <summary>Every captured message plus every scope, concatenated for a single redaction scan.</summary>
    public string AllText => string.Join(Environment.NewLine, _messages.Concat(_scopes));

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        _scopes.Enqueue(FormatScope(state));
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _messages.Enqueue(formatter(state, exception));

    private static string FormatScope<TState>(TState state)
    {
        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            return string.Join(";", pairs.Select(p => $"{p.Key}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}"));
        }

        return state?.ToString() ?? string.Empty;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
