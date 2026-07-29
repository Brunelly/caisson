using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Discovery;

/// <summary>
/// A durable, resumable, idempotent discovery run for one rack (story #8, AC1). It is a mutable
/// registry-style entity — deliberately <b>not</b> append-only — whose status is transitioned in place
/// as the runner executes its ordered <see cref="Steps"/>. Durability of the status/heartbeat/steps is
/// what lets a restarted process resume a run (NFR1) and what a DB partial-unique index uses to enforce
/// at most one active job per rack (NFR5). It carries provenance (who/what/correlation id) but
/// <b>never</b> any secret or credential material (NFR4).
/// </summary>
public sealed class DiscoveryJob
{
    /// <summary>Maximum length of the operator-safe <see cref="ErrorMessage"/>.</summary>
    public const int MaxErrorMessageLength = 2048;

    /// <summary>Maximum length of the client <see cref="IdempotencyKey"/> (matches the column bound).</summary>
    public const int MaxIdempotencyKeyLength = 200;

    private readonly List<DiscoveryJobStep> _steps = new();

    private DiscoveryJob()
    {
        // EF Core materialization constructor.
        TriggeredBy = null!;
    }

    /// <summary>Creates a queued job. Use <see cref="SeedSteps"/> to attach the standard step rows.</summary>
    public DiscoveryJob(
        Guid id,
        Guid rackId,
        TriggerType mode,
        string triggeredBy,
        ActorType actorType,
        Guid correlationId,
        DateTime createdAtUtc,
        bool dryRun = false,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(triggeredBy);

        Id = id;
        RackId = rackId;
        Mode = mode;
        Status = DiscoveryJobStatus.Queued;
        TriggeredBy = triggeredBy;
        ActorType = actorType;
        CorrelationId = correlationId;
        CreatedAtUtc = createdAtUtc;
        DryRun = dryRun;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>Stable job identifier (AC1: a stable jobId).</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack the run observes.</summary>
    public Guid RackId { get; private set; }

    /// <summary>How the run was initiated (reuses the observed-state trigger enum).</summary>
    public TriggerType Mode { get; private set; }

    /// <summary>Current durable lifecycle state.</summary>
    public DiscoveryJobStatus Status { get; private set; }

    /// <summary>When the job was created/queued.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When the runner first started executing the job.</summary>
    public DateTime? StartedAtUtc { get; private set; }

    /// <summary>When the job reached a terminal state.</summary>
    public DateTime? FinishedAtUtc { get; private set; }

    /// <summary>The user or service subject that triggered the run.</summary>
    public string TriggeredBy { get; private set; }

    /// <summary>The kind of principal that triggered the run.</summary>
    public ActorType ActorType { get; private set; }

    /// <summary>Correlation id stamped on every log line, step and audit event for this run (NFR4).</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>Optional client idempotency key; a repeat with the same key replays the same job (AC2).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Informational-only dry-run flag for M0 (no destructive ops); still recorded (AC2).</summary>
    public bool DryRun { get; private set; }

    /// <summary>Liveness heartbeat; a stale heartbeat lets the runner reclaim a crashed job (NFR1).</summary>
    public DateTime? LastHeartbeatAtUtc { get; private set; }

    /// <summary>Number of times the runner has claimed/attempted this job.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Stable machine-readable error code when the job failed.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Operator-safe error message when the job failed.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The persisted snapshot produced by the run, once persistence succeeds (idempotency guard).</summary>
    public Guid? ResultSnapshotId { get; private set; }

    /// <summary>Durable, cross-instance cancellation request flag re-read before each step (AC/Q3).</summary>
    public bool CancellationRequested { get; private set; }

    /// <summary>The ordered steps of this job.</summary>
    public IReadOnlyList<DiscoveryJobStep> Steps => _steps;

    /// <summary>Attaches one Pending step row per <see cref="DiscoveryStepName"/> in declaration order.</summary>
    public void SeedSteps(Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        foreach (var name in Enum.GetValues<DiscoveryStepName>())
        {
            _steps.Add(new DiscoveryJobStep(newId(), Id, name));
        }
    }

    /// <summary>Attaches an already-constructed step (used by EF-free callers/tests).</summary>
    public void AddStep(DiscoveryJobStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
    }

    /// <summary>Marks the job as claimed/running and refreshes the heartbeat (idempotent for resume).</summary>
    public void MarkInProgress(DateTime nowUtc)
    {
        Status = DiscoveryJobStatus.InProgress;
        StartedAtUtc ??= nowUtc;
        LastHeartbeatAtUtc = nowUtc;
        AttemptCount++;
    }

    /// <summary>Refreshes the liveness heartbeat (NFR1).</summary>
    public void Heartbeat(DateTime nowUtc) => LastHeartbeatAtUtc = nowUtc;

    /// <summary>Records the durable cancellation request (the cross-instance source of truth, Q3).</summary>
    public void RequestCancellation() => CancellationRequested = true;

    /// <summary>Records the snapshot produced by the persistence step (the idempotency guard, AC1).</summary>
    public void SetResultSnapshot(Guid snapshotId) => ResultSnapshotId = snapshotId;

    /// <summary>Transitions the job to its successful terminal state.</summary>
    public void Succeed(DateTime finishedAtUtc)
    {
        Status = DiscoveryJobStatus.Succeeded;
        LastHeartbeatAtUtc = finishedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        ErrorCode = null;
        ErrorMessage = null;
    }

    /// <summary>Transitions the job to its failed terminal state with a stable error code.</summary>
    public void Fail(DateTime finishedAtUtc, string errorCode, string? errorMessage)
    {
        Status = DiscoveryJobStatus.Failed;
        LastHeartbeatAtUtc = finishedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        ErrorCode = errorCode;
        ErrorMessage = Truncate(errorMessage);
    }

    /// <summary>Transitions the job to its canceled terminal state.</summary>
    public void Cancel(DateTime finishedAtUtc)
    {
        Status = DiscoveryJobStatus.Canceled;
        LastHeartbeatAtUtc = finishedAtUtc;
        FinishedAtUtc = finishedAtUtc;
    }

    private static string? Truncate(string? message)
    {
        // Finding #27: a value-level backstop — ErrorMessage is meant to be a fixed, operator-safe string
        // (DiscoveryErrorCodes.MessageFor), but this defends the column even if a future caller passes
        // through a less-trusted message that happens to embed secret-shaped text.
        var scrubbed = SecretScrubber.Scrub(message);
        return scrubbed is { Length: > MaxErrorMessageLength } ? scrubbed[..MaxErrorMessageLength] : scrubbed;
    }
}
