namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Broadcast on each durable discovery-job status transition (story #9, AC1): enqueue (→ Queued), claim
/// (→ InProgress) and the terminal states (Succeeded/Failed/Canceled). Emitted alongside the existing
/// audit-write points so events and audit stay consistent. <see cref="Seq"/> is a cluster-monotonic
/// per-job sequence (Redis <c>INCR</c>), so ordering holds even when enqueue and run happen on different
/// API instances; clients ignore an older <c>(jobId, seq)</c>.
/// </summary>
/// <param name="RackId">The rack the job targets.</param>
/// <param name="JobId">The discovery job.</param>
/// <param name="Status">The new status (Queued/InProgress/Succeeded/Failed/Canceled).</param>
/// <param name="PreviousStatus">The prior status, when known.</param>
/// <param name="CurrentStep">The current pipeline step, when known.</param>
/// <param name="ErrorCode">An operator-safe error code on a failed job (never a raw exception).</param>
/// <param name="Timestamp">When the transition occurred (UTC).</param>
/// <param name="Seq">The cluster-monotonic per-job ordering sequence.</param>
/// <param name="CorrelationId">The job's correlation id.</param>
public sealed record DiscoveryJobStatusChangedEvent(
    Guid RackId,
    Guid JobId,
    string Status,
    string? PreviousStatus,
    string? CurrentStep,
    string? ErrorCode,
    DateTimeOffset Timestamp,
    long Seq,
    Guid CorrelationId) : TopologyEvent;
