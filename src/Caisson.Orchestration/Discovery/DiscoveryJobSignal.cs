using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// A lightweight in-process nudge so the runner wakes immediately after an enqueue/cancel commit instead
/// of waiting for its next poll. Correctness never depends on it — a missed nudge only delays work until
/// the next poll cycle (the DB claim is the source of truth).
/// </summary>
public sealed class DiscoveryJobSignal
{
    private readonly Channel<Guid> _channel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>Signals that a job may be claimable now.</summary>
    public void Notify(Guid jobId) => _channel.Writer.TryWrite(jobId);

    /// <summary>The reader the runner awaits between polls.</summary>
    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>
/// In-process registry of cancellation sources for jobs the local runner is executing. The cancel
/// endpoint signals the source for the fast path; the durable <c>CancellationRequested</c> flag remains
/// the cross-instance source of truth (Q3).
/// </summary>
public sealed class DiscoveryCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    /// <summary>Registers and returns a fresh cancellation source linked to <paramref name="linkedToken"/>.</summary>
    public CancellationTokenSource Register(Guid jobId, CancellationToken linkedToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        _sources[jobId] = cts;
        return cts;
    }

    /// <summary>Removes and disposes the source for a completed job.</summary>
    public void Remove(Guid jobId)
    {
        if (_sources.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }

    /// <summary>Signals cancellation of a locally-running job; returns false if it is not local.</summary>
    public bool Signal(Guid jobId)
    {
        if (_sources.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }
}
