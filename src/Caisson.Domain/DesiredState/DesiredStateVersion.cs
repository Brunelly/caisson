using Caisson.Domain.Topology;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// One rack's validated desired-state envelope for one commit (story #62, AC3). This is
/// <see cref="IAppendOnly"/>: a version row is inserted once and never updated — "latest active
/// version per rack" is always DERIVED by querying the newest row per <see cref="RackSlug"/>
/// (<see cref="Caisson.Infrastructure.Persistence.Queries.LatestDesiredStateVersionQueries"/>,
/// mirroring ADR 0002's <c>ORDER BY created_at DESC, id DESC</c> tie-break for observed-state
/// snapshots), never by mutating a flag. <see cref="IsActive"/> is therefore a write-once breadcrumb
/// set <c>true</c> at insert and never flipped afterwards — it must never be read via a raw
/// <c>WHERE is_active</c> query (NFR7).
/// </summary>
/// <remarks>
/// Deliberately keyed by a plain <see cref="RackSlug"/> string, not a foreign key to the observed-state
/// <c>Rack.Id</c>: desired state must ingest independently of whether a <c>Rack</c> registry row exists
/// for that slug yet (no production path creates <c>Rack</c> rows today).
/// </remarks>
public sealed class DesiredStateVersion : IAppendOnly
{
    private DesiredStateVersion()
    {
        // EF Core materialization constructor.
        RackSlug = null!;
        CommitSha = null!;
        ContentHash = null!;
    }

    public DesiredStateVersion(
        Guid id,
        string rackSlug,
        string commitSha,
        Guid ingestionRunId,
        DateTime createdAtUtc,
        string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            throw new ArgumentException($"'{rackSlug}' is not a valid rack slug.", nameof(rackSlug));
        }

        Id = id;
        RackSlug = rackSlug;
        CommitSha = commitSha;
        IngestionRunId = ingestionRunId;
        CreatedAtUtc = createdAtUtc;
        ContentHash = contentHash;
        IsActive = true;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack this version describes (not FK'd to any observed-state <c>Rack</c> row).</summary>
    public string RackSlug { get; private set; }

    /// <summary>The Git commit SHA this version was materialised from.</summary>
    public string CommitSha { get; private set; }

    /// <summary>The ingestion run that produced this version.</summary>
    public Guid IngestionRunId { get; private set; }

    /// <summary>When this version was persisted.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Write-once breadcrumb, always <c>true</c> at insert. NEVER read directly to determine "the"
    /// active version — always go through <c>LatestDesiredStateVersionQueries</c>.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Content hash of the normalised rack definition, used to skip re-materialising an unchanged
    /// rack file on a commit that only touched other racks.
    /// </summary>
    public string ContentHash { get; private set; }
}
