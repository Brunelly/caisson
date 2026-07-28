using Caisson.Domain.Discovery;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// Runs a claimed discovery job through its four-step pipeline (switch discovery → BMC discovery →
/// correlation → persistence), transitioning each step and the job durably as it goes. It is resumable
/// (re-running the read-only/pure steps is safe and the persistence step is idempotent via
/// <see cref="DiscoveryJob.ResultSnapshotId"/>) and cooperatively cancelable (AC1, Q3).
/// </summary>
public interface IDiscoveryOrchestrator
{
    /// <summary>Executes the pipeline for an already-claimed, <c>InProgress</c> job.</summary>
    Task RunAsync(DiscoveryJob job, CancellationToken cancellationToken);
}
