namespace Caisson.Domain.NetworkConfig;

/// <summary>
/// The single, rack-scoped, mutable draft of authored network intent (story #168): a VLAN catalogue plus
/// per-port access-VLAN intent, serialized as one bounded <see cref="IntentJson"/> payload. Deliberately a
/// NEW, mutable entity — NOT part of the append-only <c>Caisson.Domain.DesiredState</c> tree, which is
/// fed exclusively by the git-ingestion pipeline (story #62) and must never be written to from an
/// interactive authoring path. The story's Q3 answer ("single saved state only") means there is exactly
/// one row per rack (enforced by a unique index on <c>RackId</c>, story #176) and no draft/publish or
/// version history. Optimistic concurrency is the row's Postgres <c>xmin</c> (via
/// <c>UseXminAsConcurrencyToken()</c> in <c>RackNetworkIntentConfiguration</c>), never a hand-rolled
/// version counter.
/// </summary>
public sealed class RackNetworkIntent
{
    /// <summary>Bound on the serialized <see cref="IntentJson"/> payload (mirrors the desired-state YAML document ceiling).</summary>
    public const int MaxIntentJsonLength = DesiredState.DesiredStateSchema.MaxYamlDocumentBytes;

    /// <summary>Maximum length of <see cref="CreatedBy"/> / <see cref="UpdatedBy"/>.</summary>
    public const int MaxActorLength = 256;

    private RackNetworkIntent()
    {
        // EF Core materialization constructor.
        IntentJson = null!;
        CreatedBy = null!;
        UpdatedBy = null!;
    }

    /// <summary>Creates the rack's first saved network-intent state.</summary>
    public RackNetworkIntent(Guid id, Guid rackId, string intentJson, string createdBy, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(intentJson);
        ArgumentException.ThrowIfNullOrEmpty(createdBy);

        Id = id;
        RackId = rackId;
        IntentJson = Bound(intentJson, MaxIntentJsonLength, nameof(intentJson));
        CreatedAtUtc = createdAtUtc;
        CreatedBy = Bound(createdBy, MaxActorLength, nameof(createdBy));
        UpdatedAtUtc = createdAtUtc;
        UpdatedBy = CreatedBy;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack this saved intent belongs to (unique per rack — single saved state, story Q3).</summary>
    public Guid RackId { get; private set; }

    /// <summary>
    /// The bounded <c>jsonb</c> payload: <c>{ vlanCatalogue: [...], portIntents: [...] }</c>. Always the
    /// output of <see cref="NetworkIntentValidator.Validate"/> having returned no errors — the controller
    /// never persists an unvalidated payload.
    /// </summary>
    public string IntentJson { get; private set; }

    /// <summary>When this rack's network intent was first saved.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The actor (user or service subject) who first saved this rack's network intent.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>When this rack's network intent was last saved.</summary>
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>The actor who last saved this rack's network intent.</summary>
    public string UpdatedBy { get; private set; }

    /// <summary>Replaces the saved payload (the story's single-saved-state PUT/upsert, story #176).</summary>
    public void Update(string intentJson, string updatedBy, DateTime updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(intentJson);
        ArgumentException.ThrowIfNullOrEmpty(updatedBy);

        IntentJson = Bound(intentJson, MaxIntentJsonLength, nameof(intentJson));
        UpdatedBy = Bound(updatedBy, MaxActorLength, nameof(updatedBy));
        UpdatedAtUtc = updatedAtUtc;
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
