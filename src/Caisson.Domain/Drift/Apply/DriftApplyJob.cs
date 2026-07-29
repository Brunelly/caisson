using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Drift.Apply;

/// <summary>
/// A durable, resumable, idempotent single-change drift-correction apply run (story #65, AC4/AC5) — the
/// first write path in Caisson. Mirrors <c>Discovery.DiscoveryJob</c>'s mutable, registry-style shape
/// (deliberately NOT <c>IAppendOnly</c>) field-for-field: status/heartbeat/steps durability is what lets a
/// restarted process resume a run and what a DB partial-unique index uses to enforce at most one active
/// job per drift item. It additionally carries the staleness anchors captured at request time (AC3, the
/// story's answered "Both" question), the switch/port/VLAN resolved during revalidation, and the
/// device-outcome idempotency checkpoint written exactly once by <see cref="RecordDeviceOutcome"/> (the
/// crash-resume guard, AC4/NFR2). Never carries secret or credential material (NFR4).
/// </summary>
public sealed class DriftApplyJob
{
    /// <summary>Maximum length of the operator-safe <see cref="ErrorMessage"/>.</summary>
    public const int MaxErrorMessageLength = 2048;

    /// <summary>Maximum length of <see cref="ErrorCategory"/>.</summary>
    public const int MaxErrorCategoryLength = 64;

    /// <summary>Maximum length of <see cref="ErrorCode"/> / <see cref="DeviceReasonCode"/>.</summary>
    public const int MaxErrorCodeLength = 128;

    /// <summary>Maximum length of the bounded <see cref="ErrorDetailsJson"/> payload.</summary>
    public const int MaxErrorDetailsJsonLength = 2048;

    /// <summary>Maximum length of <see cref="RequestedBy"/> / <see cref="ClaimedByInstanceId"/>.</summary>
    public const int MaxActorLength = 256;

    /// <summary>Maximum length of <see cref="SwitchDeviceKey"/>.</summary>
    public const int MaxSwitchDeviceKeyLength = 256;

    /// <summary>Maximum length of <see cref="SubjectKey"/> (mirrors <c>DriftSchema.MaxSubjectKeyLength</c>).</summary>
    public const int MaxSubjectKeyLength = 512;

    /// <summary>Maximum length of <see cref="PortName"/>.</summary>
    public const int MaxPortNameLength = 128;

    /// <summary>Maximum length of the bounded <see cref="BeforeStateJson"/> / <see cref="AfterStateJson"/> payloads.</summary>
    public const int MaxStateJsonLength = 4096;

    private readonly List<DriftApplyJobStep> _steps = new();

    private DriftApplyJob()
    {
        // EF Core materialization constructor.
        RequestedBy = null!;
        SubjectKey = null!;
    }

    /// <summary>
    /// Creates a pending apply job, capturing the staleness anchors observed at request time (AC3): the
    /// drift report the target item was read from, and its expected-before/expected-after values. Use
    /// <see cref="SeedSteps"/> to attach the standard step rows.
    /// </summary>
    public DriftApplyJob(
        Guid id,
        Guid rackId,
        Guid driftItemId,
        string subjectKey,
        string requestedBy,
        ActorType actorType,
        Guid correlationId,
        DateTime requestedAtUtc,
        Guid expectedDriftReportId,
        int? expectedBeforeVlan,
        int expectedAfterVlan)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestedBy);
        ArgumentException.ThrowIfNullOrEmpty(subjectKey);

        Id = id;
        RackId = rackId;
        DriftItemId = driftItemId;
        SubjectKey = Bound(subjectKey, MaxSubjectKeyLength, nameof(subjectKey));
        RequestedBy = Bound(requestedBy, MaxActorLength, nameof(requestedBy));
        ActorType = actorType;
        CorrelationId = correlationId;
        RequestedAtUtc = requestedAtUtc;
        Status = DriftApplyJobStatus.Pending;
        ExpectedDriftReportId = expectedDriftReportId;
        ExpectedBeforeVlan = expectedBeforeVlan;
        ExpectedAfterVlan = expectedAfterVlan;
    }

    /// <summary>Stable job identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack the target drift item belongs to.</summary>
    public Guid RackId { get; private set; }

    /// <summary>
    /// The stable, content-hashed <c>DriftItem.DriftItemId</c> this job applies. Deliberately a plain
    /// indexed value, NOT a foreign key: <c>DriftItem</c> rows are upserted/pruned by recompute, so the
    /// row this id pointed to at request time may no longer exist by the time the job runs — that is
    /// exactly the stale-drift condition revalidation checks for (AC3), not a referential-integrity error.
    /// </summary>
    public Guid DriftItemId { get; private set; }

    /// <summary>
    /// The drift item's <c>SubjectKey</c> (e.g. <c>DriftSubjectKeys.ForSwitchPort</c>), captured at request
    /// time. Revalidation re-resolves the LATEST report's item for this subject rather than re-querying by
    /// <see cref="DriftItemId"/> — a content-hash lookup can only ever say "found" (meaning identical
    /// content) or "not found", never "found but changed", so the subject key is what lets revalidation
    /// distinguish "still current" from "the same physical port drifted to a different value" (AC3).
    /// </summary>
    public string SubjectKey { get; private set; }

    /// <summary>The user or service subject that requested the apply.</summary>
    public string RequestedBy { get; private set; }

    /// <summary>The kind of principal that requested the apply.</summary>
    public ActorType ActorType { get; private set; }

    /// <summary>Correlation id stamped on every log line, step and audit event for this job.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>When the apply was requested.</summary>
    public DateTime RequestedAtUtc { get; private set; }

    /// <summary>Current durable lifecycle state.</summary>
    public DriftApplyJobStatus Status { get; private set; }

    /// <summary>When the runner first claimed the job.</summary>
    public DateTime? ClaimedAtUtc { get; private set; }

    /// <summary>The runner instance that currently (or most recently) holds the claim.</summary>
    public string? ClaimedByInstanceId { get; private set; }

    /// <summary>Liveness heartbeat; a stale heartbeat lets the runner reclaim a crashed job.</summary>
    public DateTime? LastHeartbeatAtUtc { get; private set; }

    /// <summary>Number of times the runner has claimed/attempted this job.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the job reached a terminal state.</summary>
    public DateTime? FinishedAtUtc { get; private set; }

    /// <summary>
    /// The <c>DriftReport.Id</c> the target item was read from at request time (the "driftSetRevision" —
    /// story Q3's "Both" answer, alongside <see cref="ExpectedBeforeVlan"/>/<see cref="ExpectedAfterVlan"/>).
    /// </summary>
    public Guid ExpectedDriftReportId { get; private set; }

    /// <summary>
    /// The observed access VLAN (<c>DriftItem.ActualValue</c>) at request time, or <c>null</c> when it was
    /// not a parseable VLAN id (e.g. the port had no access VLAN). Compared against the freshly
    /// recomputed item during revalidation (AC3).
    /// </summary>
    public int? ExpectedBeforeVlan { get; private set; }

    /// <summary>The desired access VLAN (<c>DriftItem.ExpectedValue</c>) at request time.</summary>
    public int ExpectedAfterVlan { get; private set; }

    /// <summary>The target switch's stable <c>DeviceDefinition.DeviceKey</c>, resolved during revalidation.</summary>
    public string? SwitchDeviceKey { get; private set; }

    /// <summary>The target port's stable interface name, resolved during revalidation.</summary>
    public string? PortName { get; private set; }

    /// <summary>The access VLAN id to set, resolved during revalidation.</summary>
    public int? DesiredVlanId { get; private set; }

    /// <summary>
    /// The driver's <c>SwitchChangeReasonCode</c> for the single device-mutating call this job made, set
    /// exactly once by <see cref="RecordDeviceOutcome"/> — the crash-resume idempotency checkpoint
    /// (AC4/NFR2): once non-null, the device-apply step is never re-attempted.
    /// </summary>
    public string? DeviceReasonCode { get; private set; }

    /// <summary>Whether the device change was explicitly confirmed within its confirm window.</summary>
    public bool? DeviceConfirmed { get; private set; }

    /// <summary>Bounded, secret-free <c>jsonb</c> snapshot of the port's access-VLAN state before the change.</summary>
    public string? BeforeStateJson { get; private set; }

    /// <summary>Bounded, secret-free <c>jsonb</c> snapshot of the port's access-VLAN state after the change.</summary>
    public string? AfterStateJson { get; private set; }

    /// <summary>Coarse classification of a terminal failure (e.g. Validation/StaleDrift/DeviceRejected/Infrastructure).</summary>
    public string? ErrorCategory { get; private set; }

    /// <summary>Stable machine-readable error/reason code when the job failed or found stale drift.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Operator-safe error message when the job failed or found stale drift.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Bounded, secret-scrubbed <c>jsonb</c> structured detail for a terminal outcome (e.g. compared report/item ids for stale drift).</summary>
    public string? ErrorDetailsJson { get; private set; }

    /// <summary>The ordered steps of this job.</summary>
    public IReadOnlyList<DriftApplyJobStep> Steps => _steps;

    /// <summary>Attaches one Pending step row per <see cref="DriftApplyStepName"/> in declaration order.</summary>
    public void SeedSteps(Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        foreach (var name in Enum.GetValues<DriftApplyStepName>())
        {
            _steps.Add(new DriftApplyJobStep(newId(), Id, name));
        }
    }

    /// <summary>Attaches an already-constructed step (used by EF-free callers/tests).</summary>
    public void AddStep(DriftApplyJobStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
    }

    /// <summary>Marks the job as claimed by a runner instance and refreshes the heartbeat (idempotent for resume).</summary>
    public void MarkClaimed(string claimedByInstanceId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(claimedByInstanceId);
        Status = DriftApplyJobStatus.Claimed;
        ClaimedByInstanceId = Bound(claimedByInstanceId, MaxActorLength, nameof(claimedByInstanceId));
        ClaimedAtUtc ??= nowUtc;
        LastHeartbeatAtUtc = nowUtc;
        AttemptCount++;
    }

    /// <summary>Refreshes the liveness heartbeat.</summary>
    public void Heartbeat(DateTime nowUtc) => LastHeartbeatAtUtc = nowUtc;

    /// <summary>Transitions to the Revalidating step.</summary>
    public void MarkRevalidating(DateTime nowUtc)
    {
        Status = DriftApplyJobStatus.Revalidating;
        Heartbeat(nowUtc);
    }

    /// <summary>Transitions to the Executing (device-apply) step.</summary>
    public void MarkExecuting(DateTime nowUtc)
    {
        Status = DriftApplyJobStatus.Executing;
        Heartbeat(nowUtc);
    }

    /// <summary>
    /// Persists the switch/port/VLAN resolved by a successful revalidation, so a crashed-and-resumed job
    /// re-executes DeviceApply without re-deriving them from the (possibly since-changed) drift item.
    /// </summary>
    public void ResolveTarget(string switchDeviceKey, string portName, int desiredVlanId)
    {
        ArgumentException.ThrowIfNullOrEmpty(switchDeviceKey);
        ArgumentException.ThrowIfNullOrEmpty(portName);
        SwitchDeviceKey = Bound(switchDeviceKey, MaxSwitchDeviceKeyLength, nameof(switchDeviceKey));
        PortName = Bound(portName, MaxPortNameLength, nameof(portName));
        DesiredVlanId = desiredVlanId;
    }

    /// <summary>
    /// Records the single device-mutating call's outcome — the crash-resume idempotency checkpoint
    /// (AC4/NFR2). May be called AT MOST ONCE per job: a second call (a crashed-and-resumed job that
    /// re-enters DeviceApply after already recording an outcome) is a bug in the caller's resume guard, so
    /// this throws loudly rather than silently overwriting the first recorded outcome.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a device outcome was already recorded.</exception>
    public void RecordDeviceOutcome(string reasonCode, bool confirmed, string? beforeStateJson, string? afterStateJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        if (DeviceReasonCode is not null)
        {
            throw new InvalidOperationException(
                $"Device outcome already recorded for drift-apply job '{Id}' (reasonCode={DeviceReasonCode}); " +
                "RecordDeviceOutcome may be called at most once (crash-resume idempotency guard).");
        }

        DeviceReasonCode = Bound(reasonCode, MaxErrorCodeLength, nameof(reasonCode));
        DeviceConfirmed = confirmed;
        BeforeStateJson = BoundState(beforeStateJson, nameof(beforeStateJson));
        AfterStateJson = BoundState(afterStateJson, nameof(afterStateJson));
    }

    /// <summary>Transitions the job to its successful terminal state.</summary>
    public void Complete(DateTime finishedAtUtc)
    {
        Status = DriftApplyJobStatus.Completed;
        Finish(finishedAtUtc);
        ClearError();
    }

    /// <summary>Transitions the job to its failed terminal state with a stable category/code.</summary>
    public void Fail(DateTime finishedAtUtc, string errorCategory, string errorCode, string? errorMessage, string? errorDetailsJson = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCategory);
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        Status = DriftApplyJobStatus.Failed;
        SetError(finishedAtUtc, errorCategory, errorCode, nameof(errorCode), errorMessage, errorDetailsJson);
    }

    /// <summary>
    /// Transitions the job to its terminal StaleDrift state (AC3): the target drift item was gone, or its
    /// expected-before/expected-after no longer matched what revalidation observed. No driver call is ever
    /// made on this path.
    /// </summary>
    public void MarkStaleDrift(DateTime finishedAtUtc, string reasonCode, string? errorMessage, string? errorDetailsJson = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        Status = DriftApplyJobStatus.StaleDrift;
        SetError(finishedAtUtc, "StaleDrift", reasonCode, nameof(reasonCode), errorMessage, errorDetailsJson);
    }

    private void ClearError()
    {
        ErrorCategory = null;
        ErrorCode = null;
        ErrorMessage = null;
        ErrorDetailsJson = null;
    }

    private void SetError(
        DateTime finishedAtUtc, string errorCategory, string errorCode, string errorCodeParamName,
        string? errorMessage, string? errorDetailsJson)
    {
        Finish(finishedAtUtc);
        ErrorCategory = Bound(errorCategory, MaxErrorCategoryLength, nameof(errorCategory));
        ErrorCode = Bound(errorCode, MaxErrorCodeLength, errorCodeParamName);
        ErrorMessage = TruncateMessage(errorMessage);
        ErrorDetailsJson = BoundDetails(errorDetailsJson);
    }

    private void Finish(DateTime finishedAtUtc)
    {
        FinishedAtUtc = finishedAtUtc;
        LastHeartbeatAtUtc = finishedAtUtc;
    }

    private static string Bound(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value exceeds the {maxLength}-character bound.", paramName);
        }

        return value;
    }

    private static string? BoundState(string? value, string paramName)
    {
        var scrubbed = SecretScrubber.Scrub(value);
        if (scrubbed is { Length: > MaxStateJsonLength })
        {
            throw new ArgumentException($"State JSON exceeds the {MaxStateJsonLength}-character bound.", paramName);
        }

        return scrubbed;
    }

    private static string? BoundDetails(string? detailsJson)
    {
        var scrubbed = SecretScrubber.Scrub(detailsJson);
        return scrubbed is { Length: > MaxErrorDetailsJsonLength } ? scrubbed[..MaxErrorDetailsJsonLength] : scrubbed;
    }

    private static string? TruncateMessage(string? message)
    {
        var scrubbed = SecretScrubber.Scrub(message);
        return scrubbed is { Length: > MaxErrorMessageLength } ? scrubbed[..MaxErrorMessageLength] : scrubbed;
    }
}
