namespace Caisson.Api.Contracts;

/// <summary>
/// The rack-scoped PR status projection returned to the UI (story #173, Task #213/#215). Carries the current
/// PR lifecycle state, the checks rollup, the URL/head SHA, the last-updated/last-checked timestamps, and the
/// gate booleans (<see cref="CanApply"/>/<see cref="GateReasonCode"/>) that drive the apply banner. When the
/// rack has no PR status yet, <see cref="HasPullRequest"/> is false and the gate reads <c>NoPrLinked</c> — a
/// consistent no-link representation that never leaks repository metadata.
/// </summary>
public sealed record PullRequestStatusDto(
    bool HasPullRequest,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? State,
    string? HeadSha,
    string ChecksConclusion,
    int? FailingChecksCount,
    string? ChecksSummary,
    DateTimeOffset? LastUpdated,
    DateTimeOffset? LastChecked,
    string? LastPollFailureReason,
    bool CanApply,
    string GateReasonCode);

/// <summary>A single PR status transition history entry (story #173, Task #213), from the append-only audit trail.</summary>
public sealed record PrStatusEventDto(
    Guid AuditEventId,
    DateTimeOffset OccurredAt,
    string Action,
    string ActorId,
    string? PreviousState,
    string? NewState,
    string? PreviousChecks,
    string? NewChecks,
    Guid CorrelationId);
