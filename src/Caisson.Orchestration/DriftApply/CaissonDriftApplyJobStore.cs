using Caisson.Domain.Drift;
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
    public Task<DriftItem?> FindDriftItemAsync(Guid rackId, Guid driftItemId, CancellationToken cancellationToken)
        => _context.ItemByDriftItemIdAsync(rackId, driftItemId, cancellationToken);
}
