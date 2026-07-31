using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// The rack-scoped lookups for the impact-preview diff cache (story #171, Task #197). Every query is scoped
/// by <c>rackId</c> so a cache row can never be read across racks (NFR2): the content-addressed lookup keys
/// on <c>(rackId, baselineRevisionId, candidateSha256)</c> and the by-id GET additionally filters on
/// <c>rackId</c> so a candidate id from another rack resolves to nothing (a leak-safe 404).
/// </summary>
public static class DiffCacheQueries
{
    /// <summary>
    /// Finds the cached preview for a candidate against a baseline revision within one rack, or <c>null</c>
    /// on a miss. Tracked so the caller can observe the row's stored fields verbatim.
    /// </summary>
    public static Task<DesiredStateCandidateDiffCache?> FindAsync(
        this CaissonDbContext context,
        Guid rackId,
        Guid baselineRevisionId,
        string candidateSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(candidateSha256);

        return context.DesiredStateCandidateDiffCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.RackId == rackId
                    && c.BaselineRevisionId == baselineRevisionId
                    && c.CandidateSha256 == candidateSha256,
                cancellationToken);
    }

    /// <summary>Resolves a cached preview by its id, scoped to <paramref name="rackId"/> (leak-safe GET).</summary>
    public static Task<DesiredStateCandidateDiffCache?> FindByIdForRackAsync(
        this CaissonDbContext context,
        Guid rackId,
        Guid cacheId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DesiredStateCandidateDiffCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cacheId && c.RackId == rackId, cancellationToken);
    }
}
