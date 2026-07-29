using System.Threading.Channels;
using Caisson.Infrastructure.Persistence.Drift;

namespace Caisson.Orchestration.Drift;

/// <summary>
/// A lightweight in-process nudge so <see cref="DriftRecomputeRunner"/> wakes immediately after a new
/// observed snapshot or desired revision, instead of waiting for the next <see cref="DriftScheduler"/>
/// sweep (story #64, AC4). Mirrors <c>Caisson.Orchestration.Discovery.DiscoveryJobSignal</c>'s bounded,
/// drop-newest-write channel: correctness never depends on this — the periodic scheduler sweep is the
/// correctness backstop — so a dropped notification only delays a rack's recompute until the next tick.
/// Registered as the real <see cref="IDriftRecomputeSignal"/> implementation by
/// <c>DriftServiceCollectionExtensions.AddCaissonDrift</c>, overriding the fail-open no-op default the
/// lower Infrastructure/Ingestion layers see otherwise.
/// </summary>
public sealed class DriftRecomputeSignal : IDriftRecomputeSignal
{
    private readonly Channel<Guid> _channel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <inheritdoc />
    public void Enqueue(Guid rackId) => _channel.Writer.TryWrite(rackId);

    /// <summary>The reader <see cref="DriftRecomputeRunner"/> awaits between polls.</summary>
    public ChannelReader<Guid> Reader => _channel.Reader;
}
