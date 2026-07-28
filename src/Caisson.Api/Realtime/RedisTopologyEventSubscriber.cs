using Caisson.Api.Realtime.Hubs;
using Caisson.Infrastructure.LiveUpdates;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caisson.Api.Realtime;

/// <summary>
/// One-per-instance background subscriber that relays live events from the Redis pub/sub channel to
/// SignalR clients (story #9, ADR 0014). Because every instance both subscribes to the channel AND runs
/// the SignalR Redis backplane, the relay is guarded by an EXACTLY-ONCE cluster lock
/// (<c>SET caisson:relayed:{eventId} {instanceId} NX EX 30</c>): only the instance that wins the key
/// issues the group-send, and the backplane then delivers to that group's members on every instance —
/// so an event produced on instance A reaches a client on instance B exactly once. Client-side
/// <c>(stream, seq)</c>/<c>eventId</c> de-dup is the safety net, so even a guard fault only risks a
/// duplicate, never a user-visible regression.
/// </summary>
public sealed class RedisTopologyEventSubscriber : BackgroundService
{
    private const string RelayKeyPrefix = "caisson:relayed:";
    private static readonly TimeSpan RelayKeyTtl = TimeSpan.FromSeconds(30);

    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TopologyHub, ITopologyClient> _hub;
    private readonly TopologyMetrics _metrics;
    private readonly ILogger<RedisTopologyEventSubscriber> _logger;
    private readonly RedisChannel _channel;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public RedisTopologyEventSubscriber(
        IConnectionMultiplexer redis,
        IHubContext<TopologyHub, ITopologyClient> hub,
        IOptions<RealtimeOptions> options,
        TopologyMetrics metrics,
        ILogger<RedisTopologyEventSubscriber> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        ArgumentNullException.ThrowIfNull(options);
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = RedisChannel.Literal(options.Value.EventsChannel);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(_channel);
        queue.OnMessage(message => RelayAsync(message.Message));
        _logger.LogInformation(
            "Topology event relay subscribed channel={Channel} instanceId={InstanceId}", _channel, _instanceId);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            await queue.UnsubscribeAsync();
        }
    }

    private async Task RelayAsync(RedisValue value)
    {
        var @event = TopologyEventSerialization.Deserialize(value.ToString());
        if (@event is null)
        {
            return;
        }

        // Exactly-once across the cluster: only the instance that wins the key fans the event out; the
        // SignalR backplane then delivers to the group's members on every instance.
        if (!await TryClaimRelayAsync(@event.EventId))
        {
            return;
        }

        try
        {
            switch (@event)
            {
                case SnapshotUpdatedEvent snapshot:
                    await _hub.Clients.Group(TopologyGroups.ForRack(snapshot.RackId)).SnapshotUpdated(snapshot);
                    break;
                case DiscoveryJobStatusChangedEvent status:
                    await _hub.Clients.Group(TopologyGroups.ForRack(status.RackId)).DiscoveryJobStatusChanged(status);
                    break;
                case HeartbeatEvent heartbeat:
                    await _hub.Clients.All.Heartbeat(heartbeat);
                    break;
            }

            _metrics.RecordRelayDelivery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Topology event relay failed eventType={EventType} eventId={EventId}", @event.GetType().Name, @event.EventId);
        }
    }

    private async Task<bool> TryClaimRelayAsync(Guid eventId)
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.StringSetAsync(RelayKeyPrefix + eventId.ToString("N"), _instanceId, RelayKeyTtl, When.NotExists);
        }
        catch (Exception ex)
        {
            // Fail-open: if the guard is unreachable, relay anyway — client-side de-dup absorbs a duplicate.
            _logger.LogWarning(ex, "Relay guard unavailable for eventId={EventId}; relaying without dedup.", eventId);
            return true;
        }
    }
}
