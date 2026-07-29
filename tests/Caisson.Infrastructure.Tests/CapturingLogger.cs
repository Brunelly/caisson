using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// A minimal <see cref="ILogger{T}"/> that captures every formatted log message, so NFR4's structured-
/// logging requirement (rackId/desiredRevisionId/observedSnapshotId/driftReportId/correlationId on every
/// drift computation) can be asserted directly against real log output — mirrors
/// <c>Caisson.Drivers.MikroTik.Tests.CapturingLogger</c>.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>All formatted messages emitted so far.</summary>
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _messages.Enqueue(formatter(state, exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
