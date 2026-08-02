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

        // Deduplicate BEFORE any database work. The same bucket key can legitimately be in this batch
        // twice: EvictOldest removes a key from the active generation and queues it for urgent flush, and
        // a later MarkSaturated re-adds that same key — so DrainUrgentFlush() and DetachGeneration() can
        // each yield it, with two distinct Entry objects. Folding them here keeps one durable aggregate
        // per bucket AND keeps the failure path's own key-indexed bookkeeping safe to build.
        var toFlush = Coalesce(urgent, generation);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var now = _time.GetUtcNow().UtcDateTime;
        var uncommitted = new Dictionary<DenialBucketKey, DenialOverflowAccumulator.Entry>(toFlush.Count);
        var committedCount = 0;
        Exception? failure = null;

        // Each INSERT autocommits on its own, so this batch can end up half-persisted. Track exactly which
        // entries did NOT commit: merging an already-committed entry back would replay its count, and
        // ON CONFLICT (id) DO NOTHING cannot dedupe that replay if a denial arriving after
        // DetachGeneration has meanwhile put a NEW Entry (with a NEW batch id) in the active generation —
        // MergeBack folds the old count into it and the committed total is written a second time under an
        // id the database has never seen. That is silent over-counting of a security signal.
        foreach (var (key, entry) in toFlush)
        {
            if (failure is not null)
            {
                // Stop at the first failure (a dead database will not heal mid-batch); everything after it
                // is untried, therefore uncommitted, and retried next tick with its own batch id intact.
                uncommitted[key] = entry;
                continue;
            }

            try
            {
                await AuditDenialBucketQueries.InsertOverflowAuditEventAsync(
                    context, entry.BatchId, now, entry.ActorType, key.ActorId, correlationId: Guid.Empty,
                    entry.RackId, BuildDetailsJson(key, entry), cancellationToken);
                committedCount++;
            }
            catch (Exception ex)
            {
                failure = ex;
                uncommitted[key] = entry;
            }
        }

        if (failure is null)
        {
            _logger.LogInformation("Audit denial overflow flushed bucketCount={BucketCount}.", committedCount);
            return;
        }

        // The recovery path must never throw. An exception escaping here is swallowed by ExecuteAsync as a
        // generic "tick failed", and the WHOLE batch — every principal's counts, including the ones that
        // never even got a chance — is gone, which is the opposite of what recovery is for.
        try
        {
            _logger.LogError(
                failure,
                "Audit denial overflow flush failed after {CommittedCount} bucket(s); merging {RetryCount} uncommitted bucket(s) back for retry.",
                committedCount, uncommitted.Count);
            _accumulator.MergeBack(uncommitted);
        }
        catch (Exception recoveryFailure)
        {
            _logger.LogCritical(
                recoveryFailure,
                "Audit denial overflow flush recovery failed; {LostCount} bucket(s) of overflow counts were lost.",
                uncommitted.Count);
        }
    }

    /// <summary>
    /// Merges the urgently-evicted buckets and the detached generation into ONE entry per bucket key,
    /// preserving encounter order (urgent first) so the flush order stays deterministic. Where a key
    /// appears twice, the counts are folded into the FIRST entry seen — so exactly one batch id survives
    /// per bucket and a retry of this batch stays idempotent under <c>ON CONFLICT (id) DO NOTHING</c>.
    /// </summary>
    private static List<KeyValuePair<DenialBucketKey, DenialOverflowAccumulator.Entry>> Coalesce(
        List<KeyValuePair<DenialBucketKey, DenialOverflowAccumulator.Entry>> urgent,
        IReadOnlyDictionary<DenialBucketKey, DenialOverflowAccumulator.Entry> generation)
    {
        var capacity = urgent.Count + generation.Count;
        var ordered = new List<KeyValuePair<DenialBucketKey, DenialOverflowAccumulator.Entry>>(capacity);
        var seen = new Dictionary<DenialBucketKey, DenialOverflowAccumulator.Entry>(capacity);

        void Fold(DenialBucketKey key, DenialOverflowAccumulator.Entry entry)
        {
            if (seen.TryGetValue(key, out var existing))
            {
                existing.MergeFrom(entry);
                return;
            }

            seen.Add(key, entry);
            ordered.Add(new KeyValuePair<DenialBucketKey, DenialOverflowAccumulator.Entry>(key, entry));
        }

        foreach (var (key, entry) in urgent)
        {
            Fold(key, entry);
        }

        foreach (var (key, entry) in generation)
        {
            Fold(key, entry);
        }

        return ordered;
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
