using System.Collections.Concurrent;
using Caisson.Api.Realtime.Hubs;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// <remarks>
/// Finding #2/#30: before any of that, a channel message must clear three gates, in order — HMAC
/// authenticity (<see cref="TopologyEventAuthenticity"/>), decode (<see cref="TopologyEventSerialization"/>,
/// now robust to both <see cref="System.Text.Json.JsonException"/> and <see cref="NotSupportedException"/>),
/// and plausibility (a known rack id, a seq that hasn't jumped implausibly far ahead). All three failure
/// modes are logged, counted, and dropped — never thrown — because this callback runs fire-and-forget off
/// a Redis pub/sub thread.
/// </remarks>
public sealed class RedisTopologyEventSubscriber : BackgroundService
{
    private const string RelayKeyPrefix = "caisson:relayed:";
    private static readonly TimeSpan RelayKeyTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KnownRackCacheTtl = TimeSpan.FromSeconds(30);

    // A legitimate seq stream advances by roughly one per discovery run/status change; anything jumping
    // further than this in one hop is more plausibly a bug or a forged message than real traffic, even
    // though it already passed the HMAC check (defense in depth, not the primary control).
    private const long MaxPlausibleForwardSeqJump = 10_000;

    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TopologyHub, ITopologyClient> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TopologyMetrics _metrics;
    private readonly ILogger<RedisTopologyEventSubscriber> _logger;
    private readonly RedisChannel _channel;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private readonly SemaphoreSlim _rackCacheLock = new(1, 1);
    private HashSet<Guid> _knownRackIds = new();
    private DateTime _rackCacheExpiresAtUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<Guid, long> _lastObservedSeq = new();

    public RedisTopologyEventSubscriber(
        IConnectionMultiplexer redis,
        IHubContext<TopologyHub, ITopologyClient> hub,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeOptions> options,
        TopologyMetrics metrics,
        ILogger<RedisTopologyEventSubscriber> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
        try
        {
            var verifiedJson = TopologyEventAuthenticity.Verify(value.ToString());
            if (verifiedJson is null)
            {
                _metrics.RecordRelayRejection();
                _logger.LogWarning("Topology event relay dropped: missing or invalid HMAC tag.");
                return;
            }

            var @event = TopologyEventSerialization.Deserialize(verifiedJson);
            if (@event is null)
            {
                _metrics.RecordDecodeFailure();
                _logger.LogWarning("Topology event relay dropped: payload did not decode to a known event.");
                return;
            }

            if (!await IsPlausibleAsync(@event))
            {
                _metrics.RecordRelayRejection();
                return;
            }

            // Exactly-once across the cluster: only the instance that wins the key fans the event out;
            // the SignalR backplane then delivers to the group's members on every instance.
            if (!await TryClaimRelayAsync(@event.EventId))
            {
                return;
            }

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
            _logger.LogWarning(ex, "Topology event relay failed.");
        }
    }

    /// <summary>
    /// Finding #2's defense-in-depth check, applied only to events that carry a rack/seq
    /// (<see cref="HeartbeatEvent"/> carries neither and is always plausible). A rack id that isn't in the
    /// known-rack cache, or a seq that has jumped implausibly far ahead of the last one observed for that
    /// rack's stream, is dropped rather than relayed. A cache-refresh fault fails OPEN (treat as
    /// plausible) — this is defense in depth behind the HMAC check, not the primary control, so a
    /// transient DB outage must not stop legitimate, correctly-signed events from reaching clients.
    /// </summary>
    private async Task<bool> IsPlausibleAsync(TopologyEvent @event)
    {
        var (rackId, seq) = @event switch
        {
            SnapshotUpdatedEvent snapshot => ((Guid?)snapshot.RackId, (long?)snapshot.Seq),
            DiscoveryJobStatusChangedEvent status => (status.RackId, status.Seq),
            _ => (null, null),
        };

        if (rackId is not { } id || seq is not { } value)
        {
            return true;
        }

        var knownRackIds = await GetKnownRackIdsAsync();
        if (knownRackIds is not null && !knownRackIds.Contains(id))
        {
            _logger.LogWarning("Topology event relay dropped: unknown rackId={RackId}.", id);
            return false;
        }

        var lastSeq = _lastObservedSeq.GetOrAdd(id, value);
        if (value > lastSeq + MaxPlausibleForwardSeqJump)
        {
            _logger.LogWarning(
                "Topology event relay dropped: implausible seq jump rackId={RackId} lastSeq={LastSeq} seq={Seq}.",
                id, lastSeq, value);
            return false;
        }

        if (value > lastSeq)
        {
            _lastObservedSeq[id] = value;
        }

        return true;
    }

    /// <summary>Returns the cached set of known rack ids, refreshing from the DB at most every <see cref="KnownRackCacheTtl"/>. Returns null (skip the check) if the refresh itself fails.</summary>
    private async Task<HashSet<Guid>?> GetKnownRackIdsAsync()
    {
        if (DateTime.UtcNow < _rackCacheExpiresAtUtc)
        {
            return _knownRackIds;
        }

        await _rackCacheLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < _rackCacheExpiresAtUtc)
            {
                return _knownRackIds;
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var ids = await context.Racks.AsNoTracking().Select(r => r.Id).ToListAsync();
            _knownRackIds = new HashSet<Guid>(ids);
            _rackCacheExpiresAtUtc = DateTime.UtcNow + KnownRackCacheTtl;
            return _knownRackIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Known-rack cache refresh failed; skipping the rack-id plausibility check.");
            return null;
        }
        finally
        {
            _rackCacheLock.Release();
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
