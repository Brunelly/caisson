using Caisson.Domain.Drift;
using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Orchestration.Git;

/// <summary>
/// The default <see cref="IPrMergeGate"/> (story #173, Task #213). Matches the apply target's EXACT candidate
/// fingerprint against the rack's <c>GitPullRequestLink.CandidateFingerprint</c> (both the SHA-256 primitive
/// story #172 established) and allows apply only when a persisted <c>Merged</c> status backs that exact link.
/// Fail-closed everywhere: an unresolved candidate, a missing link, or a missing/unmerged status all block.
/// <para>
/// Alignment (see ADR 0062): the desired revision the drift was computed against carries the SAME candidate
/// fingerprint story #172 recorded on the PR link, because ingestion stamps
/// <c>DesiredStateVersion.CandidateFingerprint</c> using the identical canonical
/// <c>CandidateFingerprint.Compute</c> primitive (project the materialised document via
/// <c>BaselineIntentProjection</c> → canonical render → length-framed SHA-256) that PR creation stamps on
/// <c>GitPullRequestLink.CandidateFingerprint</c>. The gate compares those two canonically-aligned values
/// directly, so a merged PR unlocks apply for its exact ingested candidate in the real pipeline. The gate
/// deliberately depends on <b>merged</b> state only — branch protection governs whether GitHub permits the
/// merge; Caisson's hard boundary is that a merge actually occurred. A revision with a <c>null</c> fingerprint
/// (pre-alignment rows, or a document the projection cannot render) fails closed as <c>NoPrLinked</c>.
/// </para>
/// </summary>
public sealed class PrMergeGate : IPrMergeGate
{
    private readonly CaissonDbContext _context;

    public PrMergeGate(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<PrMergeGateResult> EvaluateAsync(
        Guid rackId, string candidateFingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(candidateFingerprint))
        {
            return new PrMergeGateResult(PrMergeGateReason.NoPrLinked);
        }

        var linkIds = await _context.GitPullRequestLinks
            .AsNoTracking()
            .Where(l => l.RackId == rackId && l.CandidateFingerprint == candidateFingerprint)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        if (linkIds.Count == 0)
        {
            return new PrMergeGateResult(PrMergeGateReason.NoPrLinked);
        }

        var merged = await _context.GitPullRequestStatuses
            .AsNoTracking()
            .AnyAsync(
                s => linkIds.Contains(s.PullRequestLinkId) && s.State == GitPullRequestStatus.Merged,
                cancellationToken);

        return new PrMergeGateResult(merged ? PrMergeGateReason.Allowed : PrMergeGateReason.PrNotMerged);
    }

    /// <inheritdoc />
    public async Task<PrMergeGateResult> EvaluateForDriftItemAsync(DriftItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var report = await _context.ReportByIdAsync(item.RackId, item.DriftReportId, cancellationToken);
        if (report is null)
        {
            return new PrMergeGateResult(PrMergeGateReason.NoPrLinked);
        }

        var candidateFingerprint = await _context.DesiredStateVersions
            .AsNoTracking()
            .Where(v => v.Id == report.DesiredRevisionId)
            .Select(v => v.CandidateFingerprint)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrEmpty(candidateFingerprint))
        {
            return new PrMergeGateResult(PrMergeGateReason.NoPrLinked);
        }

        return await EvaluateAsync(item.RackId, candidateFingerprint, cancellationToken);
    }
}

/// <summary>
/// Thrown by the drift-apply enqueue path's defence-in-depth gate check (story #173, Task #213) when a caller
/// reaches <c>RequestApplyAsync</c> for a candidate whose PR is not merged. The controller pre-checks the gate
/// and returns a 409 before this can fire on the normal path; this is the backstop for any other caller.
/// </summary>
public sealed class PrMergeGateBlockedException : Exception
{
    public PrMergeGateBlockedException(PrMergeGateReason reason)
        : base($"Apply is blocked by the PR merge gate ({reason}).")
        => Reason = reason;

    /// <summary>The blocking gate reason.</summary>
    public PrMergeGateReason Reason { get; }

    /// <summary>The stable PascalCase reason code for the API.</summary>
    public string ReasonCode => new PrMergeGateResult(Reason).ReasonCode;
}
