using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Shaping;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The single enqueue/query seam shared by BOTH the trigger endpoint and the scheduler, so the
/// one-active-job-per-rack rule (NFR5) and idempotency semantics (AC2) have exactly one implementation.
/// Enqueue is DB-backed: the partial-unique index enforces single-active, and the idempotency-key index
/// backs replay.
/// </summary>
public interface IDiscoveryJobService
{
    /// <summary>
    /// Enqueues a new discovery job for a rack, or returns the existing job when a run is already active
    /// (conflict) or the idempotency key matches an existing job (replay).
    /// </summary>
    Task<EnqueueResult> EnqueueAsync(
        Guid rackId,
        TriggerType mode,
        string triggeredBy,
        ActorType actorType,
        Guid correlationId,
        string? idempotencyKey,
        bool dryRun,
        CancellationToken cancellationToken);

    /// <summary>Requests cancellation of a job (durable flag + fast-path signal). </summary>
    Task<CancelResult> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Returns one page of a rack's jobs (newest first), over-fetching by one for the cursor.</summary>
    Task<List<DiscoveryJob>> GetJobsPageAsync(
        Guid rackId, KeysetPosition? after, int limit, CancellationToken cancellationToken);

    /// <summary>Returns a single job with its steps, or null if unknown.</summary>
    Task<DiscoveryJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Returns when a run for the rack last succeeded, or null if never (AC4).</summary>
    Task<DateTime?> GetLastSuccessAtUtcAsync(Guid rackId, CancellationToken cancellationToken);

    /// <summary>Returns the rack's discovery status summary (latest job + last success), or null if none.</summary>
    Task<DiscoveryStatusSummary?> GetStatusAsync(Guid rackId, CancellationToken cancellationToken);
}

/// <summary>The disposition of an <see cref="IDiscoveryJobService.EnqueueAsync"/> call.</summary>
public enum EnqueueDisposition
{
    /// <summary>A new job was created and queued.</summary>
    Created = 0,

    /// <summary>A run is already active for the rack; <see cref="EnqueueResult.JobId"/> is that job (409).</summary>
    Conflict,

    /// <summary>The idempotency key matched an existing job, whose id is returned (202 replay).</summary>
    IdempotentReplay,
}

/// <summary>The result of an enqueue attempt.</summary>
/// <param name="Disposition">Whether the job was created, conflicted, or replayed.</param>
/// <param name="JobId">The created, active, or replayed job id.</param>
public sealed record EnqueueResult(EnqueueDisposition Disposition, Guid JobId);

/// <summary>The disposition of a cancellation request.</summary>
public enum CancelDisposition
{
    /// <summary>Cancellation was requested for a non-terminal job.</summary>
    Requested = 0,

    /// <summary>The job does not exist.</summary>
    NotFound,

    /// <summary>The job is already in a terminal state (409).</summary>
    AlreadyTerminal,
}

/// <summary>The result of a cancellation request.</summary>
/// <param name="Disposition">Whether cancellation was requested, not-found, or already terminal.</param>
public sealed record CancelResult(CancelDisposition Disposition);

/// <summary>A rack's at-a-glance discovery status (AC4).</summary>
/// <param name="RackId">The rack.</param>
/// <param name="LatestJob">The most recent job, if any.</param>
/// <param name="LastSuccessAtUtc">When a run for the rack last succeeded, if ever.</param>
/// <param name="ScheduleEnabled">Whether recurring discovery is enabled.</param>
/// <param name="NextRunAtUtc">When the next scheduled run is due, if scheduled.</param>
public sealed record DiscoveryStatusSummary(
    Guid RackId,
    DiscoveryJob? LatestJob,
    DateTime? LastSuccessAtUtc,
    bool ScheduleEnabled,
    DateTime? NextRunAtUtc);
