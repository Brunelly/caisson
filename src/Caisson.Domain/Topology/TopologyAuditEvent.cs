using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Topology;

/// <summary>
/// A tamper-evident audit event covering both discovery-run activity and read-API access (AC3). It is
/// <see cref="IAppendOnly"/> (never updated or deleted, NFR4) but deliberately <b>not</b>
/// <see cref="ISnapshotScoped"/>: API-access events are not bound to a snapshot, so
/// <see cref="RackId"/> and <see cref="SnapshotId"/> are nullable. Tamper-evidence is enforced by the
/// EF guard and a database <c>BEFORE UPDATE OR DELETE</c> trigger.
/// </summary>
public sealed class TopologyAuditEvent : IAppendOnly
{
    /// <summary>Maximum length of the bounded <see cref="DetailsJson"/> payload.</summary>
    public const int MaxDetailsJsonLength = 8192;

    private TopologyAuditEvent()
    {
        // EF Core materialization constructor.
        ActorId = null!;
        Action = null!;
        TargetType = null!;
        Result = null!;
    }

    /// <summary>Creates an audit event record.</summary>
    /// <exception cref="ArgumentException">Thrown when the details payload exceeds the bound.</exception>
    public TopologyAuditEvent(
        Guid id,
        DateTime occurredAtUtc,
        ActorType actorType,
        string actorId,
        string action,
        string targetType,
        Guid correlationId,
        string result,
        Guid? rackId = null,
        Guid? snapshotId = null,
        string? targetId = null,
        string? detailsJson = null)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(result);

        // Finding #27: a value-level backstop for this free-text jsonb column, since the property-name
        // guard cannot see into its content — e.g. a driver exception's text accidentally embedding a
        // connection string. Scrubbed before the length check so redaction can never push it over the bound.
        var scrubbedDetailsJson = SecretScrubber.Scrub(detailsJson);
        if (scrubbedDetailsJson is { Length: > MaxDetailsJsonLength })
        {
            throw new ArgumentException(
                $"Details JSON exceeds the {MaxDetailsJsonLength}-character bound.", nameof(detailsJson));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc;
        ActorType = actorType;
        ActorId = actorId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        Result = result;
        CorrelationId = correlationId;
        RackId = rackId;
        SnapshotId = snapshotId;
        DetailsJson = scrubbedDetailsJson;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack the event concerns, if any (null for cross-rack API access).</summary>
    public Guid? RackId { get; private set; }

    /// <summary>The snapshot the event concerns, if any.</summary>
    public Guid? SnapshotId { get; private set; }

    /// <summary>When the audited action occurred.</summary>
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>The kind of principal that performed the action.</summary>
    public ActorType ActorType { get; private set; }

    /// <summary>The principal identifier (user/service-principal id or name).</summary>
    public string ActorId { get; private set; }

    /// <summary>The action performed (e.g. <c>discovery.persisted</c>, <c>topology.latest.read</c>).</summary>
    public string Action { get; private set; }

    /// <summary>The kind of target the action addressed (e.g. <c>snapshot</c>, <c>rack</c>).</summary>
    public string TargetType { get; private set; }

    /// <summary>The target identifier, if applicable.</summary>
    public string? TargetId { get; private set; }

    /// <summary>The outcome of the action (e.g. <c>success</c>, <c>denied</c>).</summary>
    public string Result { get; private set; }

    /// <summary>Correlation id linking the event to a request or discovery run.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>Optional bounded <c>jsonb</c> details payload.</summary>
    public string? DetailsJson { get; private set; }
}
