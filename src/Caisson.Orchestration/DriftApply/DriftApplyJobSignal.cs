using System.Threading.Channels;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// A lightweight in-process nudge so the drift-apply runner wakes immediately after a request-apply
/// commit instead of waiting for its next poll. Mirrors <c>Discovery.DiscoveryJobSignal</c>: correctness
/// never depends on this — a missed nudge only delays work until the next poll cycle (the DB claim is the
/// source of truth).
/// </summary>
public sealed class DriftApplyJobSignal
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
