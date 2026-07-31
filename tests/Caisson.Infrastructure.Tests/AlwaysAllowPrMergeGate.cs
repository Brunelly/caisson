using Caisson.Domain.Drift;
using Caisson.Orchestration.Git;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// A permissive <see cref="IPrMergeGate"/> for tests that exercise drift-apply mechanics rather than the
/// story #173 merge gate itself (which has its own dedicated tests). Always returns
/// <see cref="PrMergeGateReason.Allowed"/>.
/// </summary>
public sealed class AlwaysAllowPrMergeGate : IPrMergeGate
{
    public Task<PrMergeGateResult> EvaluateAsync(Guid rackId, string candidateFingerprint, CancellationToken cancellationToken)
        => Task.FromResult(new PrMergeGateResult(PrMergeGateReason.Allowed));

    public Task<PrMergeGateResult> EvaluateForDriftItemAsync(DriftItem item, CancellationToken cancellationToken)
        => Task.FromResult(new PrMergeGateResult(PrMergeGateReason.Allowed));
}
