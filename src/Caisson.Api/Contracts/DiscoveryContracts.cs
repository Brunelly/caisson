namespace Caisson.Api.Contracts;

/// <summary>
/// Request/response contracts for the discovery orchestration endpoints (story #8). These expose only
/// status/timing/error-code/provenance fields — NEVER a credentials ref, host, port, or raw device data
/// (AC4).
/// </summary>
/// <param name="Mode">The trigger mode; only <c>OnDemand</c> is accepted from clients.</param>
/// <param name="IdempotencyKey">Optional client key; a repeat with the same key replays the same job.</param>
/// <param name="DryRun">Informational for M0 (no destructive ops); still recorded.</param>
public sealed record TriggerDiscoveryRequest(string? Mode, string? IdempotencyKey, bool DryRun = false);

/// <summary>The body returned when a discovery job is queued or replayed.</summary>
/// <param name="JobId">The created (202) or existing (replay/conflict) job id.</param>
public sealed record TriggerDiscoveryResponse(Guid JobId);

/// <summary>A discovery job summary for the rack history list (AC4).</summary>
public sealed record DiscoveryJobSummaryDto(
    Guid JobId,
    Guid RackId,
    string Mode,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string TriggeredBy,
    bool DryRun,
    string? ErrorCode,
    DateTime? LastSuccessAt);

/// <summary>One step's status within a job detail response (AC4).</summary>
public sealed record DiscoveryStepDto(
    string StepName,
    string Status,
    int AttemptCount,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    long? DurationMs,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Per-step progress and operator-safe error detail for one job (AC4).</summary>
public sealed record DiscoveryJobDetailDto(
    Guid JobId,
    Guid RackId,
    string Mode,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string TriggeredBy,
    string ActorType,
    bool DryRun,
    Guid CorrelationId,
    int AttemptCount,
    string? CurrentStep,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? ResultSnapshotId,
    IReadOnlyList<DiscoveryStepDto> Steps);

/// <summary>A rack's at-a-glance discovery status (AC4).</summary>
public sealed record DiscoveryStatusDto(
    Guid RackId,
    DiscoveryJobSummaryDto? LatestJob,
    DateTime? LastSuccessAt,
    bool ScheduleEnabled,
    DateTime? NextRunAt);

/// <summary>The recurring discovery schedule view/response (AC3/AC4).</summary>
public sealed record DiscoveryScheduleDto(
    Guid RackId,
    bool Enabled,
    int IntervalSeconds,
    int JitterSeconds,
    DateTime? NextRunAt,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt);

/// <summary>The Admin-only schedule update request (AC4).</summary>
/// <param name="Enabled">Whether recurring discovery is enabled.</param>
/// <param name="IntervalSeconds">Fixed interval between runs, in seconds.</param>
/// <param name="JitterSeconds">Maximum random jitter added to the interval, in seconds.</param>
public sealed record UpdateScheduleRequest(bool Enabled, int IntervalSeconds, int JitterSeconds);
