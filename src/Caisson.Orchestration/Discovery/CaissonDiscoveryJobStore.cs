using Caisson.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The production <see cref="IDiscoveryJobStore"/>: flushes the tracked <see cref="CaissonDbContext"/>
/// and re-reads the durable cancellation flag with a fresh scalar query (bypassing the identity map so
/// a cross-instance cancel is observed).
/// </summary>
public sealed class CaissonDiscoveryJobStore : IDiscoveryJobStore
{
    private readonly CaissonDbContext _context;

    public CaissonDiscoveryJobStore(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken)
        => await _context.DiscoveryJobs
            .Where(j => j.Id == jobId)
            .Select(j => (bool?)j.CancellationRequested)
            .FirstOrDefaultAsync(cancellationToken) ?? false;
}
