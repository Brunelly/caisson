using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Auditing;

/// <inheritdoc cref="IAuthorizationDenialAuditWriter"/>
public sealed class AuthorizationDenialAuditWriter : IAuthorizationDenialAuditWriter
{
    /// <summary>The literal action for a verbatim, durable first-N denial — story #308's architecture test
    /// asserts this string appears nowhere outside this Tier 2 implementation and its own tests.</summary>
    internal const string Action = "authorization.forbidden";

    private readonly CaissonDbContext _context;
    private readonly DenialOverflowAccumulator _accumulator;
    private readonly TimeProvider _time;
    private readonly IOptions<AuditDurabilityOptions> _options;
    private readonly AuthorizationDenialAuditMetrics _metrics;
    private readonly ILogger<AuthorizationDenialAuditWriter> _logger;

    public AuthorizationDenialAuditWriter(
        CaissonDbContext context,
        DenialOverflowAccumulator accumulator,
        TimeProvider time,
        IOptions<AuditDurabilityOptions> options,
        AuthorizationDenialAuditMetrics metrics,
        ILogger<AuthorizationDenialAuditWriter> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RecordDenialAsync(
        ActorType actorType,
        string actorId,
        string endpoint,
        string outcome,
        Guid? rackId,
        Guid correlationId,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorId);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        try
        {
            var options = _options.Value;
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var windowStart = FloorToWindow(nowUtc, options.DenialWindowSeconds);
            var windowEnd = windowStart.AddSeconds(options.DenialWindowSeconds);
            var key = new DenialBucketKey(actorId, endpoint, outcome, windowStart);

            if (_accumulator.IsKnownSaturated(key))
            {
                // The hot flood path: no DB round trip at all, bounded purely by (buckets × windows).
                _accumulator.Increment(key, nowUtc);
                return;
            }

            var bucketId = Guid.NewGuid();
            await AuditDenialBucketQueries.UpsertBucketAsync(
                _context, bucketId, actorId, actorType, endpoint, outcome, windowStart, windowEnd, nowUtc, cancellationToken);

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var bucket = await AuditDenialBucketQueries.LockBucketAsync(_context, actorId, endpoint, outcome, windowStart, cancellationToken);
            if (bucket is null)
            {
                // Defensive only — the upsert above guarantees the row exists by the time we lock it.
                await transaction.RollbackAsync(cancellationToken);
                _accumulator.MarkSaturated(key, actorType, rackId, windowEnd, nowUtc);
                return;
            }

            if (bucket.TryRecordDurableDenial(options.DenialFirstN, nowUtc))
            {
                _context.AuditEvents.Add(new TopologyAuditEvent(
                    Guid.NewGuid(), nowUtc, actorType, actorId, Action, "http-request", correlationId, outcome,
                    rackId: rackId, snapshotId: null, targetId: endpoint, detailsJson: detailsJson));
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            // Saturated: do NOT persist the bucket's LastSeenAtUtc bump — that would reintroduce writes
            // proportional to request volume. Roll back and cache saturation for the rest of the window.
            await transaction.RollbackAsync(cancellationToken);
            _accumulator.MarkSaturated(key, actorType, rackId, windowEnd, nowUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The swallow is deliberate and must stay (it mirrors the pre-existing
            // ForbidLoggingAuthorizationResultHandler contract): a denial-persistence failure must never
            // turn a 403 into a 500. But this IS the guaranteed path, so the record is now GONE — Tier 2's
            // first-N durability is contingent on the database being available (ADR 0064). Deliberately NOT
            // spilled to the Tier 1 outbox: that is the same database, so it cannot help when the database
            // is the thing that failed. Instead the loss is surfaced loudly — Error, plus a counter to
            // alert on — so it can never be a silent gap in the audit trail.
            _metrics.RecordPersistenceFailure();
            _logger.LogError(
                ex, "Authorization denial audit LOST (durable first-N write failed) actorId={ActorId} endpoint={Endpoint} correlationId={CorrelationId}",
                actorId, endpoint, correlationId);
        }
    }

    /// <summary>Floors <paramref name="nowUtc"/> to the deterministic UTC start of its <paramref name="windowSeconds"/>-wide window.</summary>
    private static DateTime FloorToWindow(DateTime nowUtc, int windowSeconds)
    {
        var epochSeconds = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var flooredEpochSeconds = epochSeconds - (epochSeconds % windowSeconds);
        return DateTimeOffset.FromUnixTimeSeconds(flooredEpochSeconds).UtcDateTime;
    }
}
