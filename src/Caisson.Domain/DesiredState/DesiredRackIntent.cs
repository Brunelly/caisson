using Caisson.Domain.Topology;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// The rack-level node of the typed desired-state tree materialised from one <see cref="DesiredStateVersion"/>
/// (story #62, AC3). Append-only: rows are inserted once per version and never updated (NFR7).
/// </summary>
public sealed class DesiredRackIntent : IAppendOnly
{
    private DesiredRackIntent()
    {
        // EF Core materialization constructor.
        RackSlug = null!;
        StableKey = null!;
    }

    public DesiredRackIntent(Guid id, Guid desiredStateVersionId, string rackSlug, string stableKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(stableKey);
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            throw new ArgumentException($"'{rackSlug}' is not a valid rack slug.", nameof(rackSlug));
        }

        Id = id;
        DesiredStateVersionId = desiredStateVersionId;
        RackSlug = rackSlug;
        StableKey = stableKey;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The version envelope this rack intent belongs to.</summary>
    public Guid DesiredStateVersionId { get; private set; }

    /// <summary>The rack slug (denormalized from the owning version for convenient querying).</summary>
    public string RackSlug { get; private set; }

    /// <summary>Stable identifier for this rack node in the desired-state tree.</summary>
    public string StableKey { get; private set; }
}
