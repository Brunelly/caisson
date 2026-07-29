using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-only, composable drift query helpers (story #64, AC5/NFR3). All queries are
/// <c>AsNoTracking</c> and bounded/keyset — no unbounded <c>ToListAsync</c> — mirroring
/// <c>SnapshotQueries</c>'s shape. Pagination is keyset by <c>(created/computed timestamp desc, id
/// desc)</c>, served by the <c>(rack_id, computed_at_utc)</c>/<c>(drift_report_id, created_at_utc)</c>
/// indexes.
/// </summary>
public static class DriftQueries
{
    /// <summary>
    /// Resolves the most recently (re)computed drift report across ALL racks, or <c>null</c> if none has
    /// been computed yet — the last-run status <c>DriftComputationHealthCheck</c> reports (mirrors
    /// <c>DesiredStateIngestionRunQueries.LatestIngestionRunAsync</c>'s global-latest shape).
    /// </summary>
    public static Task<DriftReport?> LatestReportAcrossRacksAsync(
        this CaissonDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DriftReports.AsNoTracking()
            .OrderByDescending(r => r.ComputedAtUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The timestamp of the most recent successful drift computation across all racks, if any.</summary>
    public static Task<DateTime?> LastSuccessfulComputationAtUtcAsync(
        this CaissonDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DriftReports
            .Where(r => r.Status == DriftComputationStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.ComputedAtUtc, cancellationToken);
    }

    /// <summary>Resolves the latest drift report for a rack, or <c>null</c> if none has been computed yet.</summary>
    public static Task<DriftReport?> LatestReportForRackAsync(
        this CaissonDbContext context, Guid rackId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DriftReports.AsNoTracking()
            .Where(r => r.RackId == rackId)
            .OrderByDescending(r => r.ComputedAtUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a page of drift report summaries for a rack, newest-first. Each row already carries
    /// <c>TotalItems</c>/<c>CountsBySeverityJson</c>, so no extra per-report query is needed. When
    /// <paramref name="after"/> is supplied the page continues strictly after that composite
    /// <c>(computed_at, id)</c> keyset position. One extra row is fetched so the caller can tell whether
    /// a further page exists.
    /// </summary>
    public static Task<List<DriftReport>> ReportHistoryPageAsync(
        this CaissonDbContext context, Guid rackId, KeysetPosition? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.DriftReports.AsNoTracking().Where(r => r.RackId == rackId);

        if (after is { } cursor)
        {
            query = query.Where(r =>
                r.ComputedAtUtc < cursor.TimestampUtc
                || (r.ComputedAtUtc == cursor.TimestampUtc && r.Id < cursor.Id));
        }

        return query
            .OrderByDescending(r => r.ComputedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Loads a specific drift report, scoped to its rack (a cross-rack id 404s rather than leaking data).</summary>
    public static Task<DriftReport?> ReportByIdAsync(
        this CaissonDbContext context, Guid rackId, Guid driftReportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DriftReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RackId == rackId && r.Id == driftReportId, cancellationToken);
    }

    /// <summary>
    /// Loads a filtered, keyset-paginated page of a report's items, newest-first by
    /// <c>(created_at, id)</c>. Filters are optional and additive (AC5's <c>severity</c>/<c>driftType</c>/
    /// <c>actionable</c> query parameters).
    /// </summary>
    public static Task<List<DriftItem>> ItemsPageAsync(
        this CaissonDbContext context,
        Guid driftReportId,
        DriftSeverity? severity,
        DriftType? driftType,
        bool? actionable,
        KeysetPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.DriftItems.AsNoTracking().Where(i => i.DriftReportId == driftReportId);

        if (severity is { } s)
        {
            query = query.Where(i => i.Severity == s);
        }

        if (driftType is { } t)
        {
            query = query.Where(i => i.DriftType == t);
        }

        if (actionable is { } a)
        {
            query = query.Where(i => i.Actionable == a);
        }

        if (after is { } cursor)
        {
            query = query.Where(i =>
                i.CreatedAtUtc < cursor.TimestampUtc
                || (i.CreatedAtUtc == cursor.TimestampUtc && i.Id < cursor.Id));
        }

        return query
            .OrderByDescending(i => i.CreatedAtUtc)
            .ThenByDescending(i => i.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves whether drift STILL currently holds for one subject (story #65, AC3's "Both" check):
    /// finds the rack's LATEST computed report (not just any report ever containing the id — unlike
    /// <see cref="ItemByDriftItemIdAsync"/>, which resolves a stable id even across retention/rescans),
    /// then looks for an item of the given type on that exact subject key within THAT report only.
    /// Because <see cref="DriftItem.DriftItemId"/> is itself a hash of the subject plus expected/actual
    /// values, "found in the latest report" and "still matches the anchors" are the same fact: a
    /// content-hash lookup can never observe "found but different", so revalidation must re-resolve by
    /// subject, not by the original id, to detect a since-changed expected/actual pair.
    /// </summary>
    public static async Task<DriftItem?> LatestItemBySubjectAsync(
        this CaissonDbContext context, Guid rackId, DriftSubjectType subjectType, string subjectKey, DriftType driftType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(subjectKey);

        var report = await context.LatestReportForRackAsync(rackId, cancellationToken);
        if (report is null)
        {
            return null;
        }

        return await context.DriftItems.AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.DriftReportId == report.Id && i.SubjectType == subjectType
                    && i.SubjectKey == subjectKey && i.DriftType == driftType,
                cancellationToken);
    }

    /// <summary>
    /// Resolves a drift item by its stable, content-hashed <see cref="DriftItem.DriftItemId"/>, scoped to
    /// its rack. The same <c>DriftItemId</c> may legitimately appear in more than one report (ADR 0029),
    /// so this resolves the item belonging to the LATEST report that contains it.
    /// </summary>
    public static Task<DriftItem?> ItemByDriftItemIdAsync(
        this CaissonDbContext context, Guid rackId, Guid driftItemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DriftItems.AsNoTracking()
            .Where(i => i.RackId == rackId && i.DriftItemId == driftItemId)
            .Join(
                context.DriftReports.AsNoTracking(),
                item => item.DriftReportId,
                report => report.Id,
                (item, report) => new { Item = item, report.ComputedAtUtc, report.Id })
            .OrderByDescending(x => x.ComputedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Item)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
