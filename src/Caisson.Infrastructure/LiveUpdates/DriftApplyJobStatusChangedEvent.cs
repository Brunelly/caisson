namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Broadcast on each durable drift-apply-job status transition (story #65, AC7): enqueue (→ Pending),
/// claim (→ Claimed), revalidation (→ Revalidating) and the terminal states
/// (Completed/Failed/StaleDrift/Canceled). Emitted alongside the existing audit-write points so events and
/// audit stay consistent. <see cref="Seq"/> is a cluster-monotonic per-job sequence (Redis <c>INCR</c> via
/// <see cref="TopologyStreams.ForDriftApplyJob"/>), so ordering holds even when enqueue and run happen on
/// different API instances; clients ignore an older <c>(jobId, seq)</c>.
/// </summary>
/// <param name="RackId">The rack the job targets.</param>
/// <param name="JobId">The drift-apply job.</param>
/// <param name="Status">The new status.</param>
/// <param name="PreviousStatus">The prior status, when known.</param>
/// <param name="CurrentStep">The current step name, when known (AC7's step summary).</param>
/// <param name="ReasonCode">An operator-safe reason code on a terminal outcome (e.g. a <c>SwitchChangeReasonCode</c> or stale-drift reason), never a raw exception.</param>
/// <param name="ErrorCode">An operator-safe error code on a failed job, never a raw exception.</param>
/// <param name="Timestamp">When the transition occurred (UTC).</param>
/// <param name="Seq">The cluster-monotonic per-job ordering sequence.</param>
/// <param name="CorrelationId">The job's correlation id.</param>
public sealed record DriftApplyJobStatusChangedEvent(
    Guid RackId,
    Guid JobId,
    string Status,
    string? PreviousStatus,
    string? CurrentStep,
    string? ReasonCode,
    string? ErrorCode,
    DateTimeOffset Timestamp,
    long Seq,
    Guid CorrelationId) : TopologyEvent;
