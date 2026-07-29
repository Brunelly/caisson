using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Shaping;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// The single enqueue/query seam for the drift-apply endpoint (story #65, AC1/AC5). Enqueue is DB-backed:
/// the partial-unique <c>(rack_id, drift_item_id)</c> index (filtered to non-terminal statuses) enforces
/// at most one active job per drift item, and <see cref="RequestApplyAsync"/> resolves a race on that
/// index into an idempotent <see cref="RequestApplyDisposition.ExistingActiveJob"/> reply — the story's
/// answered Q1 (200/202-with-existingJobId, never a hard 409 conflict).
/// </summary>
public interface IDriftApplyJobService
{
    /// <summary>
    /// Creates a new apply job for the given, already-validated drift item, or returns the id of the
    /// already-active job for that <c>(RackId, DriftItemId)</c> pair. The caller is responsible for all
    /// pre-creation validation (rack access, item existence, supported drift type) — this method assumes
    /// <paramref name="item"/> is a valid, currently-actionable target.
    /// </summary>
    Task<RequestApplyResult> RequestApplyAsync(
        DriftItem item, string requestedBy, ActorType actorType, Guid correlationId, CancellationToken cancellationToken);

    /// <summary>Returns a single job with its steps, or null if unknown.</summary>
    Task<DriftApplyJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one page of a rack's apply jobs (newest first), optionally filtered to a single
    /// <paramref name="status"/> — applied as a DB-level predicate so keyset pagination (the over-fetch-by-
    /// one-for-the-cursor convention) stays correct with the filter active. Over-fetches by one for the cursor.
    /// </summary>
    Task<List<DriftApplyJob>> GetJobsPageAsync(
        Guid rackId, DriftApplyJobStatus? status, KeysetPosition? after, int limit, CancellationToken cancellationToken);
}

/// <summary>The disposition of a <see cref="IDriftApplyJobService.RequestApplyAsync"/> call.</summary>
public enum RequestApplyDisposition
{
    /// <summary>A new job was created and queued.</summary>
    Created = 0,

    /// <summary>A non-terminal job already exists for this drift item; <see cref="RequestApplyResult.JobId"/> is that job.</summary>
    ExistingActiveJob,
}

/// <summary>The result of a <see cref="IDriftApplyJobService.RequestApplyAsync"/> call.</summary>
/// <param name="Disposition">Whether the job was created or an existing active job was returned.</param>
/// <param name="JobId">The created or existing active job id.</param>
public sealed record RequestApplyResult(RequestApplyDisposition Disposition, Guid JobId);
