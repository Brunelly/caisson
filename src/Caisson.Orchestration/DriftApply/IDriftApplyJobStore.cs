using Caisson.Domain.Drift;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// The minimal DB-touching seam <see cref="DriftApplyOrchestrator"/> needs, so the orchestrator's
/// step/flow logic can be unit-tested DB-free against a fake — mirrors
/// <c>Discovery.IDiscoveryJobStore</c>'s shape exactly.
/// </summary>
public interface IDriftApplyJobStore
{
    /// <summary>Persists whatever job/step mutations are currently tracked.</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a drift item by its stable <c>DriftItemId</c>, scoped to its rack — the revalidation
    /// step's re-fetch after <c>IDriftComputationService.ComputeAndPersistAsync</c> (AC3).
    /// </summary>
    Task<DriftItem?> FindDriftItemAsync(Guid rackId, Guid driftItemId, CancellationToken cancellationToken);
}
