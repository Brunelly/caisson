using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Publishes live topology events to the single Redis pub/sub channel (story #9, ADR 0014). Each event
/// is serialized to its polymorphic envelope and pushed via <c>ISubscriber.PublishAsync</c>. Honours the
/// never-throws contract: every publish is wrapped in a try/catch that logs a STRUCTURED WARNING with
/// correlationId/rackId/jobId, records a metric, and swallows the fault — a Redis outage never propagates
/// to ingestion or a discovery job (AC4/NFR3).
/// </summary>
public sealed class RedisTopologyEventPublisher : ITopologyEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisChannel _channel;
    private readonly TopologyMetrics _metrics;
    private readonly ILogger<RedisTopologyEventPublisher> _logger;

    public RedisTopologyEventPublisher(
        IConnectionMultiplexer redis,
        IOptions<RealtimeOptions> options,
        TopologyMetrics metrics,
        ILogger<RedisTopologyEventPublisher> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        ArgumentNullException.ThrowIfNull(options);
        _channel = RedisChannel.Literal(options.Value.EventsChannel);
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task PublishSnapshotUpdatedAsync(SnapshotUpdatedEvent @event, CancellationToken cancellationToken = default)
        => PublishAsync(@event, @event?.RackId, @event?.JobId, @event?.CorrelationId);

    /// <inheritdoc />
    public Task PublishJobStatusChangedAsync(DiscoveryJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => PublishAsync(@event, @event?.RackId, @event?.JobId, @event?.CorrelationId);

    /// <inheritdoc />
    public Task PublishDriftApplyJobStatusChangedAsync(DriftApplyJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => PublishAsync(@event, @event?.RackId, @event?.JobId, @event?.CorrelationId);

    /// <inheritdoc />
    public Task PublishGitPullRequestStatusChangedAsync(GitPullRequestStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => PublishAsync(@event, @event?.RackId, jobId: null, @event?.CorrelationId);

    /// <inheritdoc />
    public Task PublishHeartbeatAsync(HeartbeatEvent @event, CancellationToken cancellationToken = default)
        => PublishAsync(@event, rackId: null, jobId: null, correlationId: null);

    private async Task PublishAsync(TopologyEvent? @event, Guid? rackId, Guid? jobId, Guid? correlationId)
    {
        if (@event is null)
        {
            return;
        }

        try
        {
            var json = TopologyEventSerialization.Serialize(@event);
            // Finding #2: sign the envelope so the relay can reject anything that didn't come from a
            // holder of the HMAC key. TopologyEventAuthenticity.Sign can itself throw in Production if
            // the key is unconfigured — that's still just another publish fault, absorbed by this same
            // fail-open catch (AC4/NFR3).
            var signed = TopologyEventAuthenticity.Sign(json);
            var subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(_channel, signed).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-open (AC4/NFR3): never propagate. Log structured context and count the failure.
            _metrics.RecordPublishFailure();
            _logger.LogWarning(
                ex,
                "Live topology event publish failed (swallowed) eventType={EventType} eventId={EventId} rackId={RackId} jobId={JobId} correlationId={CorrelationId}",
                @event.GetType().Name, @event.EventId, rackId, jobId, correlationId);
        }
    }
}
