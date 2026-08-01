using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Raw-SQL persistence helpers for the Tier 1 audit outbox dispatcher (story #308, ADR 0064). The claim
/// uses the codebase's established atomic pattern <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP
/// LOCKED LIMIT n) RETURNING id</c> (modelled on <c>DiscoveryJobRunner.ClaimNextAsync</c>/
/// <c>GitPullRequestStatusQueries.ClaimDueAsync</c>): two dispatcher instances can never double-claim the
/// same due row, and an expired lease is re-claimable so a crashed dispatcher never strands a row.
/// </summary>
public static class AuditOutboxQueries
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due <c>Pending</c> outbox rows (available now,
    /// unleased or lease-expired), advancing each claimed row's <c>lease_until_utc</c>/<c>claimed_by</c>
    /// and incrementing <c>attempt_count</c> so no other dispatcher re-selects it during this tick and a
    /// crashed dispatcher's claim becomes reclaimable once the lease expires. Returns the claimed ids in
    /// claim (i.e. <c>available_at_utc</c>) order.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClaimDueAsync(
        CaissonDbContext context,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        string claimedBy,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(claimedBy);

        const string sql = @"
UPDATE audit_outbox
SET lease_until_utc = {1},
    claimed_by = {2},
    attempt_count = attempt_count + 1
WHERE id IN (
    SELECT id FROM audit_outbox
    WHERE status = 'Pending'
      AND available_at_utc <= {0}
      AND (lease_until_utc IS NULL OR lease_until_utc <= {0})
    ORDER BY available_at_utc
    FOR UPDATE SKIP LOCKED
    LIMIT {3}
)
RETURNING id AS ""Value""";

        var claimed = await context.Database
            .SqlQueryRaw<Guid>(sql, nowUtc, leaseUntilUtc, claimedBy, batchSize)
            .ToListAsync(cancellationToken);
        return claimed;
    }

    /// <summary>
    /// Projects one claimed outbox row's bounded columns straight into <c>topology_audit_event</c> using
    /// its own id as the audit event id, via <c>ON CONFLICT (id) DO NOTHING</c> — a redispatch (after a
    /// crash between this insert and marking the row <c>Dispatched</c>, or a re-claimed expired lease)
    /// creates no second row. A plain <c>INSERT ... SELECT</c> straight from <c>audit_outbox</c> (not a
    /// round trip through parameters) so the exact scrubbed/bounded values already staged on the outbox
    /// row are what land in the append-only table.
    /// </summary>
    public static Task<int> ProjectToAuditEventAsync(
        CaissonDbContext context, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = @"
INSERT INTO topology_audit_event
    (id, occurred_at_utc, actor_type, actor_id, action, target_type, target_id, result,
     correlation_id, rack_id, snapshot_id, details_json)
SELECT id, occurred_at_utc, actor_type, actor_id, action, target_type, target_id, result,
       correlation_id, rack_id, snapshot_id, details_json
FROM audit_outbox
WHERE id = {0}
ON CONFLICT (id) DO NOTHING";

        object[] parameters = { id };
        return context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Reads the health snapshot the outbox health check reports on WITHOUT touching
    /// <c>topology_audit_event</c>: pending backlog count, the oldest pending row's <c>available_at_utc</c>
    /// (age proxy), and the poisoned-row count.
    /// </summary>
    public static async Task<AuditOutboxHealth> HealthSnapshotAsync(
        CaissonDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pendingCount = await context.AuditOutboxMessages.AsNoTracking()
            .CountAsync(m => m.Status == Caisson.Domain.Auditing.AuditOutboxStatus.Pending, cancellationToken);

        var oldestPendingAvailableAtUtc = await context.AuditOutboxMessages.AsNoTracking()
            .Where(m => m.Status == Caisson.Domain.Auditing.AuditOutboxStatus.Pending)
            .OrderBy(m => m.AvailableAtUtc)
            .Select(m => (DateTime?)m.AvailableAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var poisonedCount = await context.AuditOutboxMessages.AsNoTracking()
            .CountAsync(m => m.Status == Caisson.Domain.Auditing.AuditOutboxStatus.Poisoned, cancellationToken);

        return new AuditOutboxHealth(pendingCount, oldestPendingAvailableAtUtc, poisonedCount);
    }
}

/// <summary>The DB-only health snapshot for the Tier 1 outbox dispatcher's health check.</summary>
public sealed record AuditOutboxHealth(int PendingCount, DateTime? OldestPendingAvailableAtUtc, int PoisonedCount);
