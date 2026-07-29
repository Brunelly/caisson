using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>One rack's active desired-state version together with its typed tree (story #62, AC3/AC4).</summary>
public sealed record DesiredStateVersionTree(
    DesiredStateVersion Version,
    DesiredRackIntent Rack,
    IReadOnlyList<DesiredSwitchIntent> Switches,
    IReadOnlyList<DesiredPortIntent> Ports);

/// <summary>
/// The ONLY place "what is the active desired-state version for a rack" is answered (ADR 0025, NFR7).
/// <see cref="DesiredStateVersion.IsActive"/> is a write-once breadcrumb and must never be read via a
/// raw <c>WHERE is_active</c> query — the active version is always DERIVED as the newest row per
/// <c>rackSlug</c>, ordered <c>created_at DESC, id DESC</c> (ADR 0002's tie-break), backed by the
/// <c>ix_desired_state_version_rack_slug_created_at_id</c> covering index.
/// </summary>
public static class LatestDesiredStateVersionQueries
{
    /// <summary>Resolves the active version row for one rack, or <c>null</c> if it has never ingested cleanly.</summary>
    public static Task<DesiredStateVersion?> ActiveVersionForRackAsync(
        this CaissonDbContext context, string rackSlug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);

        return context.DesiredStateVersions.AsNoTracking()
            .Where(v => v.RackSlug == rackSlug)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves every rack's active version — i.e. every rack that has ever ingested at least one clean
    /// version — using Postgres <c>DISTINCT ON</c> so exactly one (the newest) row per <c>rack_slug</c>
    /// is returned, matching the same <c>created_at DESC, id DESC</c> ordering as the single-rack query.
    /// </summary>
    public static Task<List<DesiredStateVersion>> LatestVersionPerRackAsync(
        this CaissonDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DesiredStateVersions
            .FromSqlRaw(
                "SELECT * FROM (SELECT DISTINCT ON (rack_slug) * FROM desired_state_version " +
                "ORDER BY rack_slug, created_at_utc DESC, id DESC) AS active_versions " +
                "ORDER BY rack_slug")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Loads one rack's active version together with its typed rack/switch/port intent tree, or
    /// <c>null</c> if the rack has never ingested cleanly.
    /// </summary>
    public static async Task<DesiredStateVersionTree?> ActiveVersionWithTreeAsync(
        this CaissonDbContext context, string rackSlug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);

        var version = await context.ActiveVersionForRackAsync(rackSlug, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var rack = await context.DesiredRackIntents.AsNoTracking()
            .FirstAsync(r => r.DesiredStateVersionId == version.Id, cancellationToken);

        var switches = await context.DesiredSwitchIntents.AsNoTracking()
            .Where(s => s.DesiredRackIntentId == rack.Id)
            .ToListAsync(cancellationToken);

        var switchIds = switches.Select(s => s.Id).ToList();
        var ports = await context.DesiredPortIntents.AsNoTracking()
            .Where(p => switchIds.Contains(p.DesiredSwitchIntentId))
            .ToListAsync(cancellationToken);

        return new DesiredStateVersionTree(version, rack, switches, ports);
    }
}
