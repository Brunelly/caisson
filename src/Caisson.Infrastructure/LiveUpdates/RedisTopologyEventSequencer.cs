using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// A cluster-monotonic <see cref="ITopologyEventSequencer"/> backed by Redis <c>INCR caisson:seq:{stream}</c>
/// (story #9, ADR 0014). Correct across API instances, so a job whose enqueue and run happen on different
/// instances still gets a strictly increasing per-job seq. Fail-open: on any Redis fault it falls back to
/// an in-process counter and logs, so seq allocation never aborts the caller.
/// </summary>
public sealed class RedisTopologyEventSequencer : ITopologyEventSequencer
{
    private const string KeyPrefix = "caisson:seq:";

    private readonly IConnectionMultiplexer _redis;
    private readonly InProcessTopologyEventSequencer _fallback = new();
    private readonly ILogger<RedisTopologyEventSequencer> _logger;

    public RedisTopologyEventSequencer(IConnectionMultiplexer redis, ILogger<RedisTopologyEventSequencer> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<long> NextAsync(string stream, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        try
        {
            var db = _redis.GetDatabase();
            return await db.StringIncrementAsync(KeyPrefix + stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis seq allocation failed for stream={Stream}; using in-process fallback.", stream);
            return await _fallback.NextAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }
}
