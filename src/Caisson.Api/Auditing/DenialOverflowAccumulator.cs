using System.Collections.Concurrent;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Auditing;

/// <summary>
/// The Tier 2 (durable-first-N + bounded counter) bucket key: the resolved stable actor id, the STABLE
/// <c>"{httpMethod} {routeTemplate}"</c> endpoint (never the raw path/query string), the outcome code, and
/// the deterministic UTC floor of the configured window (story #308, ADR 0064).
/// </summary>
public readonly record struct DenialBucketKey(string ActorId, string Endpoint, string Outcome, DateTime WindowStartAtUtc);

/// <summary>
/// The in-memory, bounded overflow counter for denials beyond a bucket's first N (story #308, ADR 0064).
/// Once <see cref="AuthorizationDenialAuditWriter"/> discovers (via the durable bucket row) that a bucket
/// has reached <c>DenialFirstN</c>, it calls <see cref="MarkSaturated"/> ONCE to cache that fact here —
/// every subsequent denial in the same window increments lock-free via
/// <see cref="TryIncrementIfSaturated"/> with NO further database round trip, which is what bounds write
/// volume by (buckets × windows) rather than by request volume. <see cref="AuditDenialFlushService"/>
/// periodically calls <see cref="DetachGeneration"/> to atomically swap in a fresh, empty dictionary.
/// <para>
/// A request racing that swap keeps incrementing whichever generation IT observed — but only because
/// <see cref="TryIncrementIfSaturated"/> reads <see cref="_active"/> exactly once per call. Splitting the
/// hot path into a separate saturation check and a separate increment (each with its own read of the
/// field) does NOT have that property: the swap can land between them and the increment then misses in
/// the new, empty dictionary while the caller has already committed to the no-DB path — a denial counted
/// nowhere at all. That is why the hot path is a single operation that reports whether it landed, and why
/// a <see langword="false"/> result must send the caller back to the durable path rather than being
/// treated as counted.
/// </para>
/// </summary>
public sealed class DenialOverflowAccumulator
{
    private readonly int _maxActiveBuckets;
    private readonly ILogger<DenialOverflowAccumulator> _logger;

    private volatile ConcurrentDictionary<DenialBucketKey, Entry> _active = new();

    /// <summary>
    /// Buckets evicted under capacity pressure before they could be added — drained and flushed by
    /// <see cref="AuditDenialFlushService"/> on its next tick, so an eviction is delayed, never silently
    /// discarded (story #308's "never silently discard an accumulator" requirement).
    /// </summary>
    private readonly ConcurrentQueue<KeyValuePair<DenialBucketKey, Entry>> _pendingUrgentFlush = new();

    public DenialOverflowAccumulator(IOptions<AuditDurabilityOptions> options, ILogger<DenialOverflowAccumulator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxActiveBuckets = options.Value.DenialMaxActiveBuckets;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns the number of distinct buckets currently accumulating overflow (diagnostics only).</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// True if this bucket/window is known-saturated on THIS instance right now (diagnostics only).
    /// Deliberately NOT a gate for the hot path: the answer can be stale the instant it is returned (a
    /// <see cref="DetachGeneration"/> may land immediately after), so acting on it as a separate step from
    /// the increment is exactly the race <see cref="TryIncrementIfSaturated"/> exists to close. Callers on
    /// the denial path must use <see cref="TryIncrementIfSaturated"/> instead.
    /// </summary>
    public bool IsKnownSaturated(DenialBucketKey key) => _active.ContainsKey(key);

    /// <summary>
    /// Records the first overflow denial for a bucket this instance just discovered is saturated (the
    /// bucket's durable row already shows <c>durable_count >= DenialFirstN</c>).
    /// </summary>
    public void MarkSaturated(DenialBucketKey key, ActorType actorType, Guid? rackId, DateTime windowEndAtUtc, DateTime nowUtc)
    {
        if (_active.Count >= _maxActiveBuckets && !_active.ContainsKey(key))
        {
            EvictOldest();
        }

        _active.AddOrUpdate(
            key,
            _ => new Entry(actorType, rackId, windowEndAtUtc, nowUtc),
            (_, existing) =>
            {
                existing.Record(nowUtc);
                return existing;
            });
    }

    /// <summary>
    /// The hot, lock-free flood path: increments <paramref name="key"/> if — and only if — this instance
    /// already knows the bucket is saturated, returning whether the increment actually landed. A
    /// <see langword="false"/> result means the caller must fall back to the durable path (which will
    /// re-establish saturation); it must NEVER be treated as "counted".
    /// <para>
    /// Deliberately ONE operation rather than a separate "is it saturated?" check followed by an
    /// increment. <see cref="_active"/> is captured into a local exactly once here, so a
    /// <see cref="DetachGeneration"/> landing mid-call cannot make the two disagree: the lookup and the
    /// increment always address the SAME dictionary instance, and the entry found in it is therefore
    /// either in the generation being flushed (still persisted) or in the new active one. Reading the
    /// field twice would let the check observe the old generation and the increment then miss in the new,
    /// empty one — reporting "counted" for a denial that was recorded nowhere.
    /// </para>
    /// </summary>
    public bool TryIncrementIfSaturated(DenialBucketKey key, DateTime nowUtc)
    {
        var generation = _active;
        if (!generation.TryGetValue(key, out var entry))
        {
            return false;
        }

        entry.Record(nowUtc);
        return true;
    }

    /// <summary>
    /// Atomically swaps in a fresh, empty dictionary and returns the detached (now-immutable-to-new-writes)
    /// generation for the flush service to persist. Requests racing this call keep incrementing whichever
    /// generation instance they already observed (the old one, briefly, or the new one) — either way no
    /// increment is lost.
    /// </summary>
    public IReadOnlyDictionary<DenialBucketKey, Entry> DetachGeneration()
        => Interlocked.Exchange(ref _active, new ConcurrentDictionary<DenialBucketKey, Entry>());

    /// <summary>
    /// Merges a detached generation back in after a failed flush attempt, folding any concurrent increments
    /// that landed on the CURRENT (post-swap) dictionary for the same key rather than overwriting them.
    /// </summary>
    public void MergeBack(IReadOnlyDictionary<DenialBucketKey, Entry> generation)
    {
        foreach (var (key, entry) in generation)
        {
            _active.AddOrUpdate(key, _ => entry, (_, existing) => existing.MergeFrom(entry));
        }
    }

    /// <summary>Drains and returns any buckets evicted under capacity pressure, for the flush service to persist immediately.</summary>
    public List<KeyValuePair<DenialBucketKey, Entry>> DrainUrgentFlush()
    {
        var drained = new List<KeyValuePair<DenialBucketKey, Entry>>();
        while (_pendingUrgentFlush.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    private void EvictOldest()
    {
        KeyValuePair<DenialBucketKey, Entry>? oldest = null;
        foreach (var pair in _active)
        {
            if (oldest is null || pair.Value.FirstSeenAtUtc < oldest.Value.Value.FirstSeenAtUtc)
            {
                oldest = pair;
            }
        }

        if (oldest is { } victim && _active.TryRemove(victim.Key, out var removed))
        {
            _logger.LogWarning(
                "Denial overflow accumulator at capacity ({MaxActiveBuckets}); evicting oldest bucket for urgent flush endpoint={Endpoint}.",
                _maxActiveBuckets, victim.Key.Endpoint);
            _pendingUrgentFlush.Enqueue(new KeyValuePair<DenialBucketKey, Entry>(victim.Key, removed));
        }
    }

    /// <summary>One bucket's in-memory overflow tally. A reference type so <see cref="ConcurrentDictionary{TKey,TValue}"/> entries can be mutated in place.</summary>
    public sealed class Entry
    {
        private long _count;
        private long _firstSeenTicks;
        private long _lastSeenTicks;

        public Entry(ActorType actorType, Guid? rackId, DateTime windowEndAtUtc, DateTime nowUtc)
        {
            ActorType = actorType;
            RackId = rackId;
            WindowEndAtUtc = windowEndAtUtc;
            _firstSeenTicks = nowUtc.Ticks;
            _lastSeenTicks = nowUtc.Ticks;
            _count = 1;

            // Fixed for the lifetime of this bucket/window's accumulator entry, including across a
            // failed-flush merge-back, so a retried flush is idempotent (ON CONFLICT (id) DO NOTHING).
            BatchId = Guid.NewGuid();
        }

        public ActorType ActorType { get; }

        public Guid? RackId { get; }

        public DateTime WindowEndAtUtc { get; }

        public DateTime FirstSeenAtUtc => new(Interlocked.Read(ref _firstSeenTicks), DateTimeKind.Utc);

        public DateTime LastSeenAtUtc => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

        public long Count => Interlocked.Read(ref _count);

        public Guid BatchId { get; }

        public void Record(DateTime nowUtc)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Exchange(ref _lastSeenTicks, nowUtc.Ticks);
        }

        /// <summary>Folds another generation's tally for the SAME bucket into this one (merge-back after a failed flush).</summary>
        public Entry MergeFrom(Entry other)
        {
            Interlocked.Add(ref _count, other.Count);
            if (other.FirstSeenAtUtc < FirstSeenAtUtc)
            {
                Interlocked.Exchange(ref _firstSeenTicks, other.FirstSeenAtUtc.Ticks);
            }

            if (other.LastSeenAtUtc > LastSeenAtUtc)
            {
                Interlocked.Exchange(ref _lastSeenTicks, other.LastSeenAtUtc.Ticks);
            }

            return this;
        }
    }
}
