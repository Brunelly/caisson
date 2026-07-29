using Caisson.Domain.Drift;
using Caisson.Domain.Enums;

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
    /// Resolves the LATEST computed report's item for a given subject/type, scoped to its rack — the
    /// revalidation step's re-fetch after <c>IDriftComputationService.ComputeAndPersistAsync</c> (AC3,
    /// the "Both" check). Deliberately re-resolves by subject rather than by the job's original
    /// content-hashed <c>DriftItemId</c>: a content-hash lookup can only ever say "found" (identical
    /// content) or "not found" — never "found but changed" — so a subject-scoped lookup against the
    /// LATEST report is what lets revalidation distinguish "still current" from "changed" or "resolved".
    /// </summary>
    Task<DriftItem?> FindCurrentAccessVlanItemAsync(
        Guid rackId, DriftSubjectType subjectType, string subjectKey, CancellationToken cancellationToken);
}
