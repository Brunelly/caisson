using Caisson.Api.Options;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Auditing;

/// <summary>
/// Periodically flushes <see cref="DenialOverflowAccumulator"/>'s in-memory overflow tallies into durable,
/// bounded aggregate <c>topology_audit_event</c> rows (story #308, ADR 0064, Tier 2(b)). Cloned from
/// <c>GitPullRequestStatusPoller</c> for shape (<see cref="PeriodicTimer"/> + injected
/// <see cref="TimeProvider"/>, an internal <see cref="TickAsync"/> for deterministic tests). Flushes on
/// the configured interval, drains any urgently-evicted buckets every tick (capacity pressure), and does
/// one final flush on graceful shutdown so a clean restart never loses pending overflow counts — an
/// UNGRACEFUL crash may still lose at most the current interval's overflow COUNT, the accepted trade-off
/// ADR 0064 documents.
/// </summary>
public sealed class AuditDenialFlushService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DenialOverflowAccumulator _accumulator;
    private readonly TimeProvider _time;
    private readonly IOptions<AuditDurabilityOptions> _options;
    private readonly ILogger<AuditDenialFlushService> _logger;

    public AuditDenialFlushService(
        IServiceScopeFactory scopeFactory,
        DenialOverflowAccumulator accumulator,
        TimeProvider time,
        IOptions<AuditDurabilityOptions> options,
        ILogger<AuditDenialFlushService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audit denial flush service started.");
        var period = TimeSpan.FromSeconds(_options.Value.DenialFlushIntervalSeconds);
        using var timer = new PeriodicTimer(period, _time);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit denial flush tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        // Graceful shutdown: one final best-effort flush so a clean restart never loses pending overflow.
        try
        {
            await TickAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final audit denial flush on shutdown failed.");
        }

        _logger.LogInformation("Audit denial flush service stopped.");
    }

    /// <summary>Runs one flush tick; internal so tests can drive it deterministically without the timer.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var urgent = _accumulator.DrainUrgentFlush();
        var generation = _accumulator.DetachGeneration();

        if (urgent.Count == 0 && generation.Count == 0)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var toFlush = new List<KeyValuePair<DenialBucketKey, DenialOverflowAccumulator.Entry>>(urgent.Count + generation.Count);
        toFlush.AddRange(urgent);
        toFlush.AddRange(generation);

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var (key, entry) in toFlush)
            {
                var detailsJson = BuildDetailsJson(key, entry);
                await AuditDenialBucketQueries.InsertOverflowAuditEventAsync(
                    context, entry.BatchId, now, entry.ActorType, key.ActorId, correlationId: Guid.Empty,
                    entry.RackId, detailsJson, cancellationToken);
            }

            _logger.LogInformation("Audit denial overflow flushed bucketCount={BucketCount}.", toFlush.Count);
        }
        catch (Exception ex)
        {
            // Merge everything back (urgent + the detached generation) so a transient DB fault loses
            // nothing — the next tick retries with the SAME stable batch ids (idempotent via ON CONFLICT).
            _logger.LogError(ex, "Audit denial overflow flush failed; merging {Count} bucket(s) back for retry.", toFlush.Count);
            _accumulator.MergeBack(toFlush.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
    }

    private static string BuildDetailsJson(DenialBucketKey key, DenialOverflowAccumulator.Entry entry)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorId"] = key.ActorId,
            ["endpoint"] = key.Endpoint,
            ["outcome"] = key.Outcome,
            ["windowStartAtUtc"] = key.WindowStartAtUtc,
            ["firstSeenAtUtc"] = entry.FirstSeenAtUtc,
            ["lastSeenAtUtc"] = entry.LastSeenAtUtc,
            ["count"] = entry.Count,
        });

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
