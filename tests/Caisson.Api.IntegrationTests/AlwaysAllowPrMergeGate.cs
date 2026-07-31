using Caisson.Domain.Drift;
using Caisson.Orchestration.Git;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A permissive <see cref="IPrMergeGate"/> for integration tests that exercise drift-apply mechanics rather
/// than the story #173 merge gate itself (which is covered by <c>PrMergeGateApiTests</c>). Always allows.
/// </summary>
public sealed class AlwaysAllowPrMergeGate : IPrMergeGate
{
    public Task<PrMergeGateResult> EvaluateAsync(Guid rackId, string candidateFingerprint, CancellationToken cancellationToken)
        => Task.FromResult(new PrMergeGateResult(PrMergeGateReason.Allowed));

    public Task<PrMergeGateResult> EvaluateForDriftItemAsync(DriftItem item, CancellationToken cancellationToken)
        => Task.FromResult(new PrMergeGateResult(PrMergeGateReason.Allowed));
}
