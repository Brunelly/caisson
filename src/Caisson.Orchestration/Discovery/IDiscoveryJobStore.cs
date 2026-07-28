namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The persistence seam the orchestrator uses to flush job/step transitions and to re-read the durable
/// cancellation flag. Abstracted so <see cref="DiscoveryOrchestrator"/> stays DB-free-testable: the
/// production implementation flushes the tracked <c>DbContext</c>, while unit tests supply a fake.
/// </summary>
public interface IDiscoveryJobStore
{
    /// <summary>Persists the current job/step state (a single <c>SaveChangesAsync</c> in production).</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads the durable <c>CancellationRequested</c> flag for the job — the cross-instance source of
    /// truth checked before each step (Q3).
    /// </summary>
    Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken);
}
