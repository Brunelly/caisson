using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Auditing;

/// <summary>
/// A Tier 1 (mandatory-durable) audit event staged for at-least-once dispatch to the append-only
/// <c>topology_audit_event</c> table (story #308, ADR 0064). Written into the SAME database transaction
/// as the state mutation it records — <see cref="Caisson.Infrastructure.Persistence.Auditing.IMandatoryAuditOutbox"/>
/// only <c>Add</c>s it, the mutation owner keeps the single commit — so a mutation can never commit
/// without its audit row, and a rolled-back mutation can never leave an orphan one.
/// <para>
/// <see cref="Id"/> IS the eventual <see cref="Caisson.Domain.Topology.TopologyAuditEvent.Id"/>: the
/// background dispatcher projects this row's bounded columns into a real audit event with the SAME id
/// via <c>ON CONFLICT (id) DO NOTHING</c>, which is what makes redispatch after a crash or lease expiry
/// idempotent. Deliberately NOT <see cref="Caisson.Domain.Topology.IAppendOnly"/>: the dispatcher updates
/// this row in place (lease/attempt/status) until it reaches a terminal <see cref="AuditOutboxStatus"/>.
/// </para>
/// </summary>
public sealed class AuditOutboxMessage
{
    /// <summary>Bounds mirrored from <see cref="Caisson.Domain.Topology.TopologyAuditEvent"/> so dispatch is a plain projection.</summary>
    public const int MaxActorIdLength = 256;

    /// <inheritdoc cref="MaxActorIdLength"/>
    public const int MaxActionLength = 128;

    /// <inheritdoc cref="MaxActorIdLength"/>
    public const int MaxTargetTypeLength = 64;

    /// <inheritdoc cref="MaxActorIdLength"/>
    public const int MaxTargetIdLength = 256;

    /// <inheritdoc cref="MaxActorIdLength"/>
    public const int MaxResultLength = 64;

    /// <inheritdoc cref="Caisson.Domain.Topology.TopologyAuditEvent.MaxDetailsJsonLength"/>
    public const int MaxDetailsJsonLength = Caisson.Domain.Topology.TopologyAuditEvent.MaxDetailsJsonLength;

    /// <summary>Maximum length of the sanitized, stable <see cref="FailureCode"/> — never a raw exception message.</summary>
    public const int MaxFailureCodeLength = 64;

    /// <summary>Maximum length of the dispatcher instance identifier that currently holds the lease.</summary>
    public const int MaxClaimedByLength = 128;

    private AuditOutboxMessage()
    {
        // EF Core materialization constructor.
        ActorId = null!;
        Action = null!;
        TargetType = null!;
        Result = null!;
    }

    /// <summary>
    /// Stages a Tier 1 audit event for dispatch. <paramref name="id"/> becomes the eventual
    /// <c>topology_audit_event.id</c>. Available for claim immediately unless <paramref name="availableAtUtc"/>
    /// is in the future.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the scrubbed details payload exceeds the bound.</exception>
    public AuditOutboxMessage(
        Guid id,
        DateTime occurredAtUtc,
        ActorType actorType,
        string actorId,
        string action,
        string targetType,
        string? targetId,
        Guid correlationId,
        string result,
        Guid? rackId,
        Guid? snapshotId,
        string? detailsJson,
        DateTime availableAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(result);

        // Finding #27 backstop, mirrored from TopologyAuditEvent: scrub before the length check so
        // redaction can never push the payload over the bound.
        var scrubbedDetailsJson = SecretScrubber.Scrub(detailsJson);
        if (scrubbedDetailsJson is { Length: > MaxDetailsJsonLength })
        {
            throw new ArgumentException(
                $"Details JSON exceeds the {MaxDetailsJsonLength}-character bound.", nameof(detailsJson));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc;
        ActorType = actorType;
        ActorId = Bound(actorId, MaxActorIdLength, nameof(actorId));
        Action = Bound(action, MaxActionLength, nameof(action));
        TargetType = Bound(targetType, MaxTargetTypeLength, nameof(targetType));
        TargetId = targetId is null ? null : Bound(targetId, MaxTargetIdLength, nameof(targetId));
        CorrelationId = correlationId;
        Result = Bound(result, MaxResultLength, nameof(result));
        RackId = rackId;
        SnapshotId = snapshotId;
        DetailsJson = scrubbedDetailsJson;

        Status = AuditOutboxStatus.Pending;
        AvailableAtUtc = availableAtUtc;
        AttemptCount = 0;
        LeaseUntilUtc = null;
        ClaimedBy = null;
        DispatchedAtUtc = null;
        FailureCode = null;
    }

    /// <summary>Primary key — also the id the dispatched <see cref="Caisson.Domain.Topology.TopologyAuditEvent"/> gets.</summary>
    public Guid Id { get; private set; }

    /// <summary>When the audited mutation occurred.</summary>
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>The kind of principal that performed the action.</summary>
    public ActorType ActorType { get; private set; }

    /// <summary>The principal identifier.</summary>
    public string ActorId { get; private set; }

    /// <summary>The action performed (e.g. <c>network-intent.saved</c>).</summary>
    public string Action { get; private set; }

    /// <summary>The kind of target the action addressed.</summary>
    public string TargetType { get; private set; }

    /// <summary>The target identifier, if applicable.</summary>
    public string? TargetId { get; private set; }

    /// <summary>Correlation id linking the event to the originating request/job.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>The outcome of the action.</summary>
    public string Result { get; private set; }

    /// <summary>The rack the event concerns, if any.</summary>
    public Guid? RackId { get; private set; }

    /// <summary>The snapshot the event concerns, if any.</summary>
    public Guid? SnapshotId { get; private set; }

    /// <summary>Bounded, secret-scrubbed <c>jsonb</c> details payload.</summary>
    public string? DetailsJson { get; private set; }

    /// <summary>The dispatch lifecycle state.</summary>
    public AuditOutboxStatus Status { get; private set; }

    /// <summary>The earliest time this row may be claimed again (immediate, or a backoff horizon after a transient failure).</summary>
    public DateTime AvailableAtUtc { get; private set; }

    /// <summary>Number of claim attempts made so far.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>While claimed, the time the lease expires; an expired lease is re-claimable by any dispatcher.</summary>
    public DateTime? LeaseUntilUtc { get; private set; }

    /// <summary>The dispatcher instance id currently holding the lease, if any (operator diagnostics only).</summary>
    public string? ClaimedBy { get; private set; }

    /// <summary>When this row was successfully dispatched.</summary>
    public DateTime? DispatchedAtUtc { get; private set; }

    /// <summary>The sanitized, stable failure code recorded once <see cref="Status"/> reaches <see cref="AuditOutboxStatus.Poisoned"/>.</summary>
    public string? FailureCode { get; private set; }

    /// <summary>
    /// Records a successful dispatch. A poisoned row can never reach this state (defence in depth: the
    /// dispatcher's own query never selects poisoned rows, but this guards direct callers too).
    /// </summary>
    public void MarkDispatched(DateTime atUtc)
    {
        if (Status == AuditOutboxStatus.Poisoned)
        {
            throw new InvalidOperationException("A poisoned audit outbox row can never be marked Dispatched.");
        }

        Status = AuditOutboxStatus.Dispatched;
        DispatchedAtUtc = atUtc;
        LeaseUntilUtc = null;
        ClaimedBy = null;
    }

    /// <summary>Releases a failed claim back to <see cref="AuditOutboxStatus.Pending"/> at a backoff horizon for retry.</summary>
    public void ReleaseForRetry(DateTime nextAvailableAtUtc)
    {
        if (Status == AuditOutboxStatus.Poisoned)
        {
            throw new InvalidOperationException("A poisoned audit outbox row can never be retried automatically.");
        }

        Status = AuditOutboxStatus.Pending;
        AvailableAtUtc = nextAvailableAtUtc;
        LeaseUntilUtc = null;
        ClaimedBy = null;
    }

    /// <summary>
    /// Marks this row permanently unable to dispatch after exhausting retries. The full payload is
    /// retained (never deleted); only a sanitized, stable code is stored — never a raw exception message.
    /// </summary>
    public void MarkPoisoned(string failureCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureCode);

        Status = AuditOutboxStatus.Poisoned;
        FailureCode = Bound(failureCode, MaxFailureCodeLength, nameof(failureCode));
        LeaseUntilUtc = null;
        ClaimedBy = null;
    }

    private static string Bound(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value exceeds the {maxLength}-character bound.", paramName);
        }

        return value;
    }
}
