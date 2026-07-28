using System.Collections.Concurrent;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The default <see cref="ITopologyEventSequencer"/> — a per-process monotonic counter per stream. It is
/// correct for single-instance/dev; the Redis-backed sequencer supersedes it in a multi-instance
/// deployment (story #9, ADR 0014). Never throws.
/// </summary>
public sealed class InProcessTopologyEventSequencer : ITopologyEventSequencer
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<long> NextAsync(string stream, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        var value = _counters.AddOrUpdate(stream, 1L, static (_, current) => current + 1L);
        return ValueTask.FromResult(value);
    }
}
