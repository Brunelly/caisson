namespace Caisson.Api.Contracts;

/// <summary>
/// Request/response contracts for the drift-apply endpoints (story #65). A single scalar
/// <see cref="DriftItemId"/> field makes "multiple drift items in one request" structurally impossible.
/// These expose only status/timing/reason-code/before-after-VLAN fields — NEVER a credentials ref, host,
/// port, or raw device diagnostics (AC6/NFR4).
/// </summary>
/// <param name="DriftItemId">The single, already-computed drift item to apply.</param>
public sealed record ApplyDriftCorrectionRequest(Guid DriftItemId);

/// <summary>The body returned when a drift-apply job is created (201) or an active job already exists (202).</summary>
/// <param name="JobId">The created or existing active job id.</param>
public sealed record ApplyDriftCorrectionResponse(Guid JobId);

/// <summary>A drift-apply job summary for the rack listing endpoint.</summary>
public sealed record DriftApplyJobSummaryDto(
    Guid JobId,
    Guid RackId,
    Guid DriftItemId,
    string Status,
    DateTime RequestedAt,
    DateTime? FinishedAt,
    string RequestedBy,
    string? ErrorCategory,
    string? ErrorCode);

/// <summary>One step's status within a drift-apply job detail response.</summary>
public sealed record DriftApplyStepDto(
    string StepName,
    string Status,
    int AttemptCount,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    long? DurationMs,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Per-step progress, resolved target, and operator-safe terminal outcome for one drift-apply job (AC4/AC6).
/// <see cref="BeforeState"/>/<see cref="AfterState"/> are the driver's typed, secret-free access-VLAN
/// snapshots (never credentials or raw device diagnostics).
/// </summary>
public sealed record DriftApplyJobDetailDto(
    Guid JobId,
    Guid RackId,
    Guid DriftItemId,
    string Status,
    DateTime RequestedAt,
    DateTime? ClaimedAt,
    DateTime? FinishedAt,
    string RequestedBy,
    string ActorType,
    Guid CorrelationId,
    int AttemptCount,
    string? CurrentStep,
    string? SwitchDeviceKey,
    string? PortName,
    int? DesiredVlanId,
    string? DeviceReasonCode,
    bool? DeviceConfirmed,
    string? BeforeState,
    string? AfterState,
    string? ErrorCategory,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<DriftApplyStepDto> Steps);
