using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// One revision's metadata ONLY — no <see cref="DesiredStateVersion.DesiredStateJson"/> — for the
/// history list view (story #63, AC3/NFR3). Deliberately a projection, not the entity itself, so
/// <see cref="DesiredStateRevisionQueries.RevisionHistoryPageAsync"/> never pulls the (potentially large)
/// payload column off the wire for a list of many rows.
/// </summary>
public sealed record DesiredStateRevisionMetadata(
    Guid Id,
    string RackSlug,
    string CommitSha,
    DateTime CreatedAtUtc,
    string? AuthorName,
    string? AuthorEmail,
    DateTime? AuthorWhenUtc,
    string ContentHash,
    int SchemaVersion,
    string IngestedBy);

/// <summary>
/// Bounded, keyset-paginated reads over <see cref="DesiredStateVersion"/> revision history (story #63,
/// AC3), sibling to <see cref="DesiredStateIngestionRunQueries"/> and
/// <see cref="LatestDesiredStateVersionQueries"/>. Every query here is either explicitly capped
/// (<c>.Take(limit)</c>) or scoped to a single row by rack + identifier — never an unbounded
/// <c>ToListAsync</c> (hardening invariant).
/// </summary>
public static class DesiredStateRevisionQueries
{
    /// <summary>
    /// A keyset page of one rack's revision metadata, newest-first (<c>created_at_utc DESC, id DESC</c>,
    /// ADR 0002's tie-break), served by the existing
    /// <c>ix_desired_state_version_rack_slug_created_at_id</c> covering index. Metadata only — the
    /// payload is never selected here (AC3, NFR3).
    /// </summary>
    public static Task<List<DesiredStateRevisionMetadata>> RevisionHistoryPageAsync(
        this CaissonDbContext context, string rackSlug, KeysetPosition? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);

        var query = context.DesiredStateVersions.AsNoTracking().Where(v => v.RackSlug == rackSlug);
        if (after is { } cursor)
        {
            query = query.Where(v =>
                v.CreatedAtUtc < cursor.TimestampUtc
                || (v.CreatedAtUtc == cursor.TimestampUtc && v.Id < cursor.Id));
        }

        return query
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.Id)
            .Take(limit)
            .Select(v => new DesiredStateRevisionMetadata(
                v.Id, v.RackSlug, v.CommitSha, v.CreatedAtUtc, v.AuthorName, v.AuthorEmail, v.AuthorWhenUtc,
                v.ContentHash, v.SchemaVersion, v.IngestedBy))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// One rack's revision by id, together with its full payload — filtered by BOTH
    /// <paramref name="rackSlug"/> and <paramref name="revisionId"/> so a revision id belonging to
    /// another rack resolves to <c>null</c> rather than leaking cross-rack data (NFR1).
    /// </summary>
    public static Task<DesiredStateVersion?> RevisionByIdAsync(
        this CaissonDbContext context, string rackSlug, Guid revisionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);

        return context.DesiredStateVersions.AsNoTracking()
            .Where(v => v.RackSlug == rackSlug && v.Id == revisionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// One rack's revision by commit SHA, together with its full payload, via the
    /// <c>ix_desired_state_version_rack_slug_commit_sha</c> index — rack-scoped for the same cross-rack
    /// isolation reason as <see cref="RevisionByIdAsync"/>.
    /// </summary>
    public static Task<DesiredStateVersion?> RevisionByCommitShaAsync(
        this CaissonDbContext context, string rackSlug, string commitSha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);

        return context.DesiredStateVersions.AsNoTracking()
            .Where(v => v.RackSlug == rackSlug && v.CommitSha == commitSha)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
