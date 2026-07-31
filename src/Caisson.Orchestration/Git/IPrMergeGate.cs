using Caisson.Domain.Drift;
using Caisson.Domain.Git;

namespace Caisson.Orchestration.Git;

/// <summary>The gate decision for an apply/promote attempt.</summary>
public enum PrMergeGateReason
{
    /// <summary>The exact candidate's PR is merged; apply is allowed.</summary>
    Allowed,

    /// <summary>No PR is linked for this exact candidate.</summary>
    NoPrLinked,

    /// <summary>A PR is linked for this candidate but is not merged.</summary>
    PrNotMerged,
}

/// <summary>The result of an <see cref="IPrMergeGate"/> evaluation.</summary>
/// <param name="Reason">The gate decision.</param>
public sealed record PrMergeGateResult(PrMergeGateReason Reason)
{
    /// <summary>Whether apply/promote may proceed (subject to normal RBAC).</summary>
    public bool Allowed => Reason == PrMergeGateReason.Allowed;

    /// <summary>The stable PascalCase reason code (<see cref="GitPrGateReasonCodes"/>) for the API/DTO.</summary>
    public string ReasonCode => Reason switch
    {
        PrMergeGateReason.Allowed => GitPrGateReasonCodes.Allowed,
        PrMergeGateReason.NoPrLinked => GitPrGateReasonCodes.NoPrLinked,
        PrMergeGateReason.PrNotMerged => GitPrGateReasonCodes.PrNotMerged,
        _ => GitPrGateReasonCodes.NoPrLinked,
    };
}

/// <summary>
/// The single source of truth for the internal "no apply/promote until the exact candidate's PR is merged"
/// safety boundary (story #173, Task #213, AC4). Used by BOTH the read DTO (drives the UI gate banner) and the
/// write path (drift-apply enforcement + defence-in-depth). It resolves the apply target's EXACT candidate
/// (never "latest PR for rack") so an unrelated merged PR cannot unlock an older/unrelated candidate, and is
/// fail-closed on missing/unknown/stale status. Designed so a future <c>desired-state/promote</c> endpoint
/// reuses <see cref="EvaluateAsync"/> unchanged.
/// </summary>
public interface IPrMergeGate
{
    /// <summary>
    /// Core evaluation for an exact candidate fingerprint on a rack: <see cref="PrMergeGateReason.NoPrLinked"/>
    /// when no PR link exists for that fingerprint, <see cref="PrMergeGateReason.PrNotMerged"/> when a link
    /// exists but no persisted <c>Merged</c> status backs it, else <see cref="PrMergeGateReason.Allowed"/>.
    /// </summary>
    Task<PrMergeGateResult> EvaluateAsync(Guid rackId, string candidateFingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the drift-apply target's exact candidate (DriftItem → DriftReport.DesiredRevisionId →
    /// DesiredStateVersion.ContentHash) and evaluates the gate for it. Fail-closed
    /// (<see cref="PrMergeGateReason.NoPrLinked"/>) when the candidate cannot be resolved.
    /// </summary>
    Task<PrMergeGateResult> EvaluateForDriftItemAsync(DriftItem item, CancellationToken cancellationToken);
}
