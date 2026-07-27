namespace Caisson.Domain.Topology;

/// <summary>
/// A minimal derived summary of how a snapshot differs from the previous snapshot for the same rack.
/// One-to-one with its <see cref="TopologySnapshot"/>. Change counts are stored as a bounded
/// <c>jsonb</c> payload; richer diffing arrives in a later milestone.
/// </summary>
public sealed class TopologyChangeSummary : ISnapshotScoped
{
    /// <summary>Maximum length of the bounded <see cref="ChangeCountsJson"/> payload.</summary>
    public const int MaxChangeCountsJsonLength = 8192;

    private TopologyChangeSummary()
    {
        // EF Core materialization constructor.
        ChangeCountsJson = null!;
    }

    /// <summary>Creates a change summary for a snapshot.</summary>
    /// <exception cref="ArgumentException">Thrown when the payload exceeds the bound.</exception>
    public TopologyChangeSummary(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        string changeCountsJson,
        Guid? previousSnapshotId = null)
    {
        if (changeCountsJson.Length > MaxChangeCountsJsonLength)
        {
            throw new ArgumentException(
                $"Change-counts JSON exceeds the {MaxChangeCountsJsonLength}-character bound.",
                nameof(changeCountsJson));
        }

        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        ChangeCountsJson = changeCountsJson;
        PreviousSnapshotId = previousSnapshotId;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>The previous snapshot compared against, if any.</summary>
    public Guid? PreviousSnapshotId { get; private set; }

    /// <summary>Bounded <c>jsonb</c> payload of per-entity change counts.</summary>
    public string ChangeCountsJson { get; private set; }
}
