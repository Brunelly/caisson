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
        DesiredStateJson = null!;
        IngestedBy = null!;
    }

    /// <summary>Length of a lowercase SHA-256 hex digest (the canonical candidate fingerprint).</summary>
    public const int CandidateFingerprintHexLength = 64;

    /// <summary>
    /// Creates a new revision row (story #63, AC1). Author fields are tolerated as <c>null</c> — a git
    /// commit that omits committer identity (e.g. a synthetic/anonymised source) must still ingest
    /// cleanly — while <paramref name="desiredStateJson"/>/<paramref name="schemaVersion"/>/
    /// <paramref name="ingestedBy"/> are always required: every version this pipeline persists has a
    /// materialised payload and a known schema/ingesting identity.
    /// </summary>
    public DesiredStateVersion(
        Guid id,
        string rackSlug,
        string commitSha,
        Guid ingestionRunId,
        DateTime createdAtUtc,
        string contentHash,
        string desiredStateJson,
        int schemaVersion,
        string ingestedBy,
        string? authorName = null,
        string? authorEmail = null,
        DateTime? authorWhenUtc = null,
        string? candidateFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        ArgumentException.ThrowIfNullOrEmpty(desiredStateJson);
        ArgumentException.ThrowIfNullOrEmpty(ingestedBy);
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            throw new ArgumentException($"'{rackSlug}' is not a valid rack slug.", nameof(rackSlug));
        }

        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema version must be at least 1.");
        }

        if (desiredStateJson.Length > DesiredStateSchema.MaxDesiredStateJsonLength)
        {
            throw new ArgumentException(
                $"Desired-state payload exceeds the {DesiredStateSchema.MaxDesiredStateJsonLength}-character bound.",
                nameof(desiredStateJson));
        }

        if (ingestedBy.Length > DesiredStateSchema.MaxIngestedByLength)
        {
            throw new ArgumentException(
                $"'{ingestedBy}' exceeds the {DesiredStateSchema.MaxIngestedByLength}-character bound.",
                nameof(ingestedBy));
        }

        if (authorName is { Length: > 0 } && authorName.Length > DesiredStateSchema.MaxAuthorNameLength)
        {
            throw new ArgumentException(
                $"'{authorName}' exceeds the {DesiredStateSchema.MaxAuthorNameLength}-character bound.",
                nameof(authorName));
        }

        if (authorEmail is { Length: > 0 } && authorEmail.Length > DesiredStateSchema.MaxAuthorEmailLength)
        {
            throw new ArgumentException(
                $"'{authorEmail}' exceeds the {DesiredStateSchema.MaxAuthorEmailLength}-character bound.",
                nameof(authorEmail));
        }

        if (candidateFingerprint is { Length: > 0 } && candidateFingerprint.Length != CandidateFingerprintHexLength)
        {
            throw new ArgumentException(
                $"The candidate fingerprint must be a {CandidateFingerprintHexLength}-character lowercase SHA-256 hex digest.",
                nameof(candidateFingerprint));
        }

        Id = id;
        RackSlug = rackSlug;
        CommitSha = commitSha;
        IngestionRunId = ingestionRunId;
        CreatedAtUtc = createdAtUtc;
        ContentHash = contentHash;
        DesiredStateJson = desiredStateJson;
        SchemaVersion = schemaVersion;
        IngestedBy = ingestedBy;
        AuthorName = authorName;
        AuthorEmail = authorEmail;
        AuthorWhenUtc = authorWhenUtc;
        CandidateFingerprint = string.IsNullOrEmpty(candidateFingerprint) ? null : candidateFingerprint;
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

    /// <summary>
    /// The <em>canonical</em> candidate fingerprint of this revision — the SHA-256 of the candidate's canonical
    /// rendered YAML, computed via the SAME <c>CandidateFingerprint</c> primitive story #172 stamps on a
    /// <c>GitPullRequestLink</c> (story #173, ADR 0062). This is what the merged-apply gate matches against a
    /// merged PR link, so it must be aligned across the ingestion↔PR-creation boundary — hence a distinct value
    /// from <see cref="ContentHash"/> (which is the raw, unframed hash of the ingested file bytes). Nullable:
    /// revisions ingested before this column existed, or a file the canonical importer cannot round-trip, carry
    /// <c>null</c>, which the gate treats as "no aligned fingerprint" (fail-closed).
    /// </summary>
    public string? CandidateFingerprint { get; private set; }

    /// <summary>
    /// The deterministic, canonically-serialized desired-state payload materialised from this commit's
    /// validated document (story #63, AC1) — the full snapshot returned by the by-id/by-commit/current
    /// read APIs; never returned by list/history views (NFR3).
    /// </summary>
    public string DesiredStateJson { get; private set; }

    /// <summary>The desired-state payload schema version this row was ingested under (<see cref="DesiredStateSchema.CurrentSchemaVersion"/> at ingest time).</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>The service-principal identity that performed this ingestion (never a user identity).</summary>
    public string IngestedBy { get; private set; }

    /// <summary>The git commit author's display name, when the commit carries one; <c>null</c> if git omits it.</summary>
    public string? AuthorName { get; private set; }

    /// <summary>The git commit author's email, when the commit carries one; <c>null</c> if git omits it.</summary>
    public string? AuthorEmail { get; private set; }

    /// <summary>The git commit author's authored-at timestamp, when the commit carries one; <c>null</c> if git omits it.</summary>
    public DateTime? AuthorWhenUtc { get; private set; }
}
