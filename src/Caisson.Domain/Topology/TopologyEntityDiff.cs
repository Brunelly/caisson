using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Topology;

/// <summary>
/// A durable, queryable per-entity diff between a rack's previous snapshot and the snapshot this row is
/// scoped to (AC2). It is <see cref="ISnapshotScoped"/> — <see cref="SnapshotId"/> is the "to" snapshot,
/// so the append-only snapshot guard covers it — and <see cref="IAppendOnly"/> so it is never mutated or
/// deleted (NFR4). A rack's first snapshot produces all-<see cref="ChangeType.Added"/> rows with a null
/// <see cref="PreviousSnapshotId"/>. Diffs are idempotent by construction (unchanged entities produce no
/// row) and backed by a unique <c>(snapshot_id, entity_type, entity_stable_key)</c> index.
/// </summary>
public sealed class TopologyEntityDiff : ISnapshotScoped, IAppendOnly
{
    /// <summary>Maximum length of the bounded <see cref="DiffPayloadJson"/> payload.</summary>
    public const int MaxDiffPayloadJsonLength = 8192;

    private TopologyEntityDiff()
    {
        // EF Core materialization constructor.
        EntityStableKey = null!;
        DiffPayloadJson = null!;
    }

    /// <summary>Creates a per-entity diff record.</summary>
    /// <exception cref="ArgumentException">Thrown when the payload exceeds the bound.</exception>
    public TopologyEntityDiff(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        TopologyEntityType entityType,
        string entityStableKey,
        ChangeType changeType,
        string diffPayloadJson,
        DateTime createdAtUtc,
        Guid correlationId,
        Guid? previousSnapshotId = null)
    {
        ArgumentNullException.ThrowIfNull(entityStableKey);
        ArgumentNullException.ThrowIfNull(diffPayloadJson);

        // Finding #27: a value-level backstop for this free-text jsonb column — the diffed fields are
        // device-reported (e.g. a switch model/hostname string), so a rogue device could otherwise smuggle
        // secret-shaped text into a persisted, queryable diff row.
        var scrubbedPayload = SecretScrubber.Scrub(diffPayloadJson)!;
        if (scrubbedPayload.Length > MaxDiffPayloadJsonLength)
        {
            throw new ArgumentException(
                $"Diff payload JSON exceeds the {MaxDiffPayloadJsonLength}-character bound.",
                nameof(diffPayloadJson));
        }

        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        PreviousSnapshotId = previousSnapshotId;
        EntityType = entityType;
        EntityStableKey = entityStableKey;
        ChangeType = changeType;
        DiffPayloadJson = scrubbedPayload;
        CreatedAtUtc = createdAtUtc;
        CorrelationId = correlationId;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    /// <remarks>The "to" snapshot this diff was computed for.</remarks>
    public Guid SnapshotId { get; private set; }

    /// <summary>The "from" snapshot compared against; <c>null</c> for a rack's first snapshot.</summary>
    public Guid? PreviousSnapshotId { get; private set; }

    /// <summary>The kind of observed entity this diff describes.</summary>
    public TopologyEntityType EntityType { get; private set; }

    /// <summary>The entity's canonical stable key (see <c>StableKeys</c>).</summary>
    public string EntityStableKey { get; private set; }

    /// <summary>Whether the entity was added, removed, or modified.</summary>
    public ChangeType ChangeType { get; private set; }

    /// <summary>Bounded <c>jsonb</c> payload of the changed fields (old/new values).</summary>
    public string DiffPayloadJson { get; private set; }

    /// <summary>When the diff was computed.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Correlation id of the discovery run that produced the diff.</summary>
    public Guid CorrelationId { get; private set; }
}
