using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;

namespace Caisson.Orchestration.DriftApply;

/// <summary>The production <see cref="IDriftApplyJobStore"/>: a thin wrapper over <see cref="CaissonDbContext"/>.</summary>
public sealed class CaissonDriftApplyJobStore : IDriftApplyJobStore
{
    private readonly CaissonDbContext _context;

    public CaissonDriftApplyJobStore(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DriftItem?> FindCurrentAccessVlanItemAsync(
        Guid rackId, DriftSubjectType subjectType, string subjectKey, CancellationToken cancellationToken)
        => _context.LatestItemBySubjectAsync(rackId, subjectType, subjectKey, DriftType.AccessVlanMismatch, cancellationToken);
}
