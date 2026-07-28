using Caisson.Infrastructure.LiveUpdates;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caisson.Api.Realtime;

/// <summary>
/// Publishes a server heartbeat through the live-updates channel every <c>HeartbeatSeconds</c> (story #9,
/// Q2), so clients can mark data stale after 30s of silence (AC2). Production is guarded by a per-interval
/// cluster lock (<c>SET caisson:heartbeat:{bucket} NX EX</c>) so exactly one instance emits per interval;
/// the relay's own exactly-once guard then delivers that single heartbeat to all clients cluster-wide.
/// </summary>
public sealed class TopologyHeartbeatService : BackgroundService
{
    private const string HeartbeatKeyPrefix = "caisson:heartbeat:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ITopologyEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly int _heartbeatSeconds;
    private readonly ILogger<TopologyHeartbeatService> _logger;

    public TopologyHeartbeatService(
        IConnectionMultiplexer redis,
        ITopologyEventPublisher publisher,
        IOptions<RealtimeOptions> options,
        TimeProvider time,
        ILogger<TopologyHeartbeatService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _heartbeatSeconds = Math.Max(1, options.Value.HeartbeatSeconds);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Topology heartbeat service started intervalSeconds={IntervalSeconds}", _heartbeatSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_heartbeatSeconds), _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _time.GetUtcNow();
            var bucket = now.ToUnixTimeSeconds() / _heartbeatSeconds;
            if (!await TryClaimBeatAsync(bucket))
            {
                return;
            }

            await _publisher.PublishHeartbeatAsync(new HeartbeatEvent(now), cancellationToken);
        }
        catch (Exception ex)
        {
            // Never let a heartbeat failure crash the host (NFR3).
            _logger.LogWarning(ex, "Heartbeat emission failed (swallowed).");
        }
    }

    private async Task<bool> TryClaimBeatAsync(long bucket)
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.StringSetAsync(
                HeartbeatKeyPrefix + bucket.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Environment.MachineName,
                TimeSpan.FromSeconds(_heartbeatSeconds * 2),
                When.NotExists);
        }
        catch
        {
            // Fail-open: if the guard is unreachable, still beat (a duplicate heartbeat is harmless).
            return true;
        }
    }
}
