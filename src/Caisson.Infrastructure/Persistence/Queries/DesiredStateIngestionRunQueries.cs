using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-only query helpers for desired-state ingestion runs and validation errors (story #62, AC4).
/// Pagination is keyset by <c>(started_at desc, id desc)</c> / <c>(created_at desc, id desc)</c> —
/// ADR 0002's deterministic tie-break — served by the covering indexes added in
/// <c>DesiredStateIngestionRunConfiguration</c>/<c>DesiredStateValidationErrorConfiguration</c>.
/// </summary>
public static class DesiredStateIngestionRunQueries
{
    /// <summary>The most recently started ingestion run overall, or <c>null</c> if none has ever run.</summary>
    public static Task<DesiredStateIngestionRun?> LatestIngestionRunAsync(
        this CaissonDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.DesiredStateIngestionRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The most recent time any run fully succeeded (AC4's "last successful ingestion time").</summary>
    public static Task<DateTime?> LastSuccessfulIngestionAtUtcAsync(
        this CaissonDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.DesiredStateIngestionRuns
            .Where(r => r.Status == IngestionRunStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.CompletedAtUtc, cancellationToken);
    }

    /// <summary>A keyset page of ingestion runs, newest-first.</summary>
    public static Task<List<DesiredStateIngestionRun>> IngestionRunsPageAsync(
        this CaissonDbContext context, KeysetPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.DesiredStateIngestionRuns.AsNoTracking();
        if (after is { } cursor)
        {
            query = query.Where(r =>
                r.StartedAtUtc < cursor.TimestampUtc
                || (r.StartedAtUtc == cursor.TimestampUtc && r.Id < cursor.Id));
        }

        return query
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>A keyset page of validation errors, optionally scoped to one run, newest-first.</summary>
    public static Task<List<DesiredStateValidationError>> ValidationErrorsPageAsync(
        this CaissonDbContext context, Guid? runId, KeysetPosition? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.DesiredStateValidationErrors.AsNoTracking();
        if (runId is { } id)
        {
            query = query.Where(e => e.IngestionRunId == id);
        }

        if (after is { } cursor)
        {
            query = query.Where(e =>
                e.CreatedAtUtc < cursor.TimestampUtc
                || (e.CreatedAtUtc == cursor.TimestampUtc && e.Id < cursor.Id));
        }

        return query
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
