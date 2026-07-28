using Caisson.Domain.Topology;
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
    /// <paramref name="afterOccurredAtUtc"/> is supplied the page continues strictly after that keyset
    /// position. One extra row is fetched so the caller can decide whether a further page exists.
    /// </summary>
    public static Task<List<TopologyAuditEvent>> AuditPageAsync(
        this CaissonDbContext context, Guid rackId, DateTime fromUtc, DateTime toUtc,
        DateTime? afterOccurredAtUtc, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.AuditEvents.AsNoTracking()
            .Where(a => a.RackId == rackId && a.OccurredAtUtc >= fromUtc && a.OccurredAtUtc < toUtc);

        if (afterOccurredAtUtc is { } cursor)
        {
            query = query.Where(a => a.OccurredAtUtc < cursor);
        }

        return query
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
