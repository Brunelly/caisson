using Caisson.Domain.Enums;

namespace Caisson.Domain.Auditing;

/// <summary>
/// Tier 2 (durable-first-N) bucket for authorization denials (story #308, ADR 0064). One row exists per
/// <c>(actor, endpoint, outcome, window)</c> — a globally-unique bucket key enforced by a unique index on
/// <c>(actor_id, endpoint, outcome, window_start_at_utc)</c> — so concurrent cold requests from any API
/// replica serialize on this ROW (insert via <c>ON CONFLICT DO NOTHING</c>, then lock it) rather than on
/// an in-process counter, which is what makes the first-N guarantee hold GLOBALLY across replicas.
/// <para>
/// <see cref="Endpoint"/> MUST be a stable route template (e.g. <c>PUT /api/racks/{rackId}/network-intent</c>),
/// never the raw request path or query string — the caller controls those, and using them as part of the
/// bucket key would make bucket cardinality (and so write volume) attacker-controlled.
/// </para>
/// <para>
/// Deliberately mutable (not <see cref="Caisson.Domain.Topology.IAppendOnly"/>): <see cref="DurableCount"/>
/// and <see cref="LastSeenAtUtc"/> are updated in place as denials arrive within the window.
/// </para>
/// </summary>
public sealed class AuditDenialBucket
{
    /// <summary>Maximum length of the resolved, stable actor id.</summary>
    public const int MaxActorIdLength = 256;

    /// <summary>Maximum length of the normalized <c>METHOD route-template</c> endpoint key.</summary>
    public const int MaxEndpointLength = 256;

    /// <summary>Maximum length of the outcome code (e.g. <c>403</c>).</summary>
    public const int MaxOutcomeLength = 16;

    private AuditDenialBucket()
    {
        // EF Core materialization constructor.
        ActorId = null!;
        Endpoint = null!;
        Outcome = null!;
    }

    /// <summary>First-sights a bucket for the current window. <see cref="DurableCount"/> starts at zero.</summary>
    public AuditDenialBucket(
        Guid id,
        string actorId,
        ActorType actorType,
        string endpoint,
        string outcome,
        DateTime windowStartAtUtc,
        DateTime windowEndAtUtc,
        DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorId);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        Id = id;
        ActorId = Bound(actorId, MaxActorIdLength, nameof(actorId));
        ActorType = actorType;
        Endpoint = Bound(endpoint, MaxEndpointLength, nameof(endpoint));
        Outcome = Bound(outcome, MaxOutcomeLength, nameof(outcome));
        WindowStartAtUtc = windowStartAtUtc;
        WindowEndAtUtc = windowEndAtUtc;
        FirstSeenAtUtc = atUtc;
        LastSeenAtUtc = atUtc;
        DurableCount = 0;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The resolved, stable actor id (never a display name that could collide/rotate).</summary>
    public string ActorId { get; private set; }

    /// <summary>The kind of principal.</summary>
    public ActorType ActorType { get; private set; }

    /// <summary>The stable <c>METHOD route-template</c> key — never the raw path or query string.</summary>
    public string Endpoint { get; private set; }

    /// <summary>The denial outcome code (e.g. <c>403</c>).</summary>
    public string Outcome { get; private set; }

    /// <summary>The deterministic UTC floor of the configured window this bucket covers.</summary>
    public DateTime WindowStartAtUtc { get; private set; }

    /// <summary>The end of the window this bucket covers (start + configured window length).</summary>
    public DateTime WindowEndAtUtc { get; private set; }

    /// <summary>When the first denial in this bucket/window was observed.</summary>
    public DateTime FirstSeenAtUtc { get; private set; }

    /// <summary>When the most recent denial in this bucket/window was observed.</summary>
    public DateTime LastSeenAtUtc { get; private set; }

    /// <summary>
    /// How many of the first-N verbatim <c>authorization.forbidden</c> events this bucket has already
    /// durably recorded (bounded by the configured <c>DenialFirstN</c>).
    /// </summary>
    public int DurableCount { get; private set; }

    /// <summary>
    /// Attempts to record one more durable, verbatim denial in this bucket. The caller MUST have already
    /// locked this row (e.g. <c>SELECT ... FOR UPDATE</c>) before calling, so the check-then-increment
    /// below is race-free across concurrent replicas. Returns <c>false</c> once <paramref name="firstN"/>
    /// is reached — the caller should then route the denial to the in-memory overflow accumulator instead.
    /// </summary>
    public bool TryRecordDurableDenial(int firstN, DateTime atUtc)
    {
        LastSeenAtUtc = atUtc;

        if (DurableCount >= firstN)
        {
            return false;
        }

        DurableCount += 1;
        return true;
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
