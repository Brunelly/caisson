using Caisson.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Raw-SQL persistence helpers for the Tier 2 (durable-first-N) authorization-denial bucket (story #308,
/// ADR 0064). The bucket row is the serialization point that makes the first-N guarantee GLOBAL across
/// API replicas: <see cref="UpsertBucketAsync"/> first-sights it with <c>ON CONFLICT ... DO NOTHING</c>
/// (same idiom as <c>GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync</c>), then
/// <see cref="LockBucketAsync"/> locks the (new-or-existing) row via <c>SELECT ... FOR UPDATE</c> so a
/// concurrent cold request from any replica serializes on it before deciding whether to write the
/// verbatim denial event.
/// </summary>
public static class AuditDenialBucketQueries
{
    /// <summary>
    /// First-sights the bucket row for a <c>(actorId, endpoint, outcome, windowStartAtUtc)</c> key.
    /// Race-safe across replicas via <c>ON CONFLICT DO NOTHING</c> against the unique bucket-key index;
    /// the losing caller simply locks the winner's row next.
    /// </summary>
    public static Task<int> UpsertBucketAsync(
        CaissonDbContext context,
        Guid id,
        string actorId,
        ActorType actorType,
        string endpoint,
        string outcome,
        DateTime windowStartAtUtc,
        DateTime windowEndAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = @"
INSERT INTO audit_denial_bucket
    (id, actor_id, actor_type, endpoint, outcome, window_start_at_utc, window_end_at_utc,
     first_seen_at_utc, last_seen_at_utc, durable_count)
VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {7}, 0)
ON CONFLICT (actor_id, endpoint, outcome, window_start_at_utc) DO NOTHING";

        object[] parameters = { id, actorId, actorType.ToString(), endpoint, outcome, windowStartAtUtc, windowEndAtUtc, nowUtc };
        return context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Locks the bucket row for <c>(actorId, endpoint, outcome, windowStartAtUtc)</c> via
    /// <c>SELECT ... FOR UPDATE</c>, returning it as a normally-tracked entity so the caller can call
    /// <c>TryRecordDurableDenial</c> and persist the result with a plain <c>SaveChangesAsync</c>. MUST be
    /// called inside an explicit transaction — the lock is released at COMMIT/ROLLBACK.
    /// </summary>
    public static Task<Domain.Auditing.AuditDenialBucket?> LockBucketAsync(
        CaissonDbContext context, string actorId, string endpoint, string outcome, DateTime windowStartAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = @"
SELECT * FROM audit_denial_bucket
WHERE actor_id = {0} AND endpoint = {1} AND outcome = {2} AND window_start_at_utc = {3}
FOR UPDATE";

        return context.AuditDenialBuckets
            .FromSqlRaw(sql, actorId, endpoint, outcome, windowStartAtUtc)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Projects one flushed overflow aggregate straight into <c>topology_audit_event</c> using the flush
    /// batch id as the audit event id, via <c>ON CONFLICT (id) DO NOTHING</c> — a retried flush of the
    /// same batch (after a transient failure) can never double-count or duplicate the row.
    /// </summary>
    public static Task<int> InsertOverflowAuditEventAsync(
        CaissonDbContext context,
        Guid batchId,
        DateTime occurredAtUtc,
        ActorType actorType,
        string actorId,
        Guid correlationId,
        Guid? rackId,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // details_json is {6}::jsonb — Npgsql infers a bare string parameter as text, which Postgres
        // refuses to insert into a jsonb column without an explicit cast.
        const string sql = @"
INSERT INTO topology_audit_event
    (id, occurred_at_utc, actor_type, actor_id, action, target_type, target_id, result,
     correlation_id, rack_id, snapshot_id, details_json)
VALUES ({0}, {1}, {2}, {3}, 'authorization.forbidden.overflow', 'http-request', NULL, '403',
        {4}, {5}, NULL, {6}::jsonb)
ON CONFLICT (id) DO NOTHING";

        // A bare DBNull.Value loses its target CLR type, which EF's raw-SQL parameter binder needs to
        // build the parameter — explicit NpgsqlParameters carry the type through even when the value is null.
        object[] parameters =
        {
            batchId,
            occurredAtUtc,
            actorType.ToString(),
            actorId,
            correlationId,
            new Npgsql.NpgsqlParameter { Value = (object?)rackId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            new Npgsql.NpgsqlParameter { Value = (object?)detailsJson ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
        };
        return context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }
}
