using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-only audit-trail queries (AC3): audit events for a rack within a time range, newest-first,
/// keyset-paginated. Served by the <c>(rack_id, occurred_at desc)</c> index. Returns both discovery
/// events and API-access events (the audit writer records the latter). Composable and
/// <c>AsNoTracking</c>.
/// </summary>
public static class AuditQueries
{
    /// <summary>
    /// Loads a page of audit events for a rack within <c>[from, to)</c>, newest-first. When
    /// <paramref name="after"/> is supplied the page continues strictly after that composite
    /// <c>(occurred_at, id)</c> keyset position — the id tie-break is applied so audit rows that share a
    /// boundary <c>occurred_at</c> (e.g. a discovery event and several API-access reads at the same tick)
    /// are never skipped across a page boundary. One extra row is fetched so the caller can decide whether
    /// a further page exists.
    /// </summary>
    public static Task<List<TopologyAuditEvent>> AuditPageAsync(
        this CaissonDbContext context, Guid rackId, DateTime fromUtc, DateTime toUtc,
        KeysetPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.AuditEvents.AsNoTracking()
            .Where(a => a.RackId == rackId && a.OccurredAtUtc >= fromUtc && a.OccurredAtUtc < toUtc);

        if (after is { } cursor)
        {
            query = query.Where(a =>
                a.OccurredAtUtc < cursor.TimestampUtc
                || (a.OccurredAtUtc == cursor.TimestampUtc && a.Id < cursor.Id));
        }

        return query
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a page of a rack's PR status transition audit events (<c>git.pr.*</c> actions), newest-first
    /// (story #173, Task #213). Keyset-paginated on the same composite <c>(occurred_at, id)</c> position as
    /// <see cref="AuditPageAsync"/>. Over-fetches by one for the cursor.
    /// </summary>
    public static Task<List<TopologyAuditEvent>> GitPrAuditPageAsync(
        this CaissonDbContext context, Guid rackId, KeysetPosition? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.AuditEvents.AsNoTracking()
            .Where(a => a.RackId == rackId && a.Action.StartsWith("git.pr."));

        if (after is { } cursor)
        {
            query = query.Where(a =>
                a.OccurredAtUtc < cursor.TimestampUtc
                || (a.OccurredAtUtc == cursor.TimestampUtc && a.Id < cursor.Id));
        }

        return query
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
