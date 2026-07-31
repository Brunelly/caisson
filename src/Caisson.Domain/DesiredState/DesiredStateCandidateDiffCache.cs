namespace Caisson.Domain.DesiredState;

/// <summary>
/// One durably-cached impact-preview diff for a rack (story #171, AC2; Task #197). Keyed by
/// <c>(RackId, BaselineRevisionId, CandidateSha256)</c> so identical candidate content against the same
/// baseline revision is computed once and re-served without recomputation, and so a preview can never be
/// retrieved across racks (NFR2). A new baseline revision changes the key and correctly invalidates stale
/// previews.
/// <para>
/// Deliberately a MUTABLE POCO with private setters and NOT <see cref="Topology.IAppendOnly"/>: the TTL
/// pruner must be able to DELETE expired rows, which the <c>DbContext.GuardAppendOnly</c> sweep would
/// otherwise block. The returned <c>candidateId</c> IS this row's <see cref="Id"/> — the speculative
/// separate candidate-authoring table from the story's data model is deferred (YAGNI: no persisted
/// candidate-authoring entity exists). See ADR 0054.
/// </para>
/// </summary>
public sealed class DesiredStateCandidateDiffCache
{
    /// <summary>Length of a lowercase SHA-256 hex digest.</summary>
    public const int Sha256HexLength = 64;

    /// <summary>Maximum length of <see cref="CreatedBy"/>.</summary>
    public const int MaxActorLength = 256;

    /// <summary>Bound on the stored raw unified diff (worst case ~2× a bounded canonical-YAML document).</summary>
    public const int MaxRawUnifiedDiffLength = 2 * DesiredStateSchema.MaxYamlDocumentBytes;

    /// <summary>Bound on the stored structured-summary JSON payload.</summary>
    public const int MaxStructuredSummaryJsonLength = DesiredStateSchema.MaxYamlDocumentBytes;

    private DesiredStateCandidateDiffCache()
    {
        // EF Core materialization constructor.
        CandidateSha256 = null!;
        BaselineSha256 = null!;
        RawUnifiedDiff = null!;
        StructuredSummaryJson = null!;
        CreatedBy = null!;
    }

    /// <summary>Creates a new cache row for a freshly-computed impact preview.</summary>
    public DesiredStateCandidateDiffCache(
        Guid id,
        Guid rackId,
        Guid baselineRevisionId,
        string candidateSha256,
        string baselineSha256,
        string rawUnifiedDiff,
        string structuredSummaryJson,
        string createdBy,
        DateTime createdAtUtc,
        DateTime? expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateSha256);
        ArgumentException.ThrowIfNullOrEmpty(baselineSha256);
        ArgumentNullException.ThrowIfNull(rawUnifiedDiff);
        ArgumentException.ThrowIfNullOrEmpty(structuredSummaryJson);
        ArgumentException.ThrowIfNullOrEmpty(createdBy);

        Id = id;
        RackId = rackId;
        BaselineRevisionId = baselineRevisionId;
        CandidateSha256 = Bound(candidateSha256, Sha256HexLength, nameof(candidateSha256));
        BaselineSha256 = Bound(baselineSha256, Sha256HexLength, nameof(baselineSha256));
        RawUnifiedDiff = Bound(rawUnifiedDiff, MaxRawUnifiedDiffLength, nameof(rawUnifiedDiff));
        StructuredSummaryJson = Bound(structuredSummaryJson, MaxStructuredSummaryJsonLength, nameof(structuredSummaryJson));
        CreatedBy = Bound(createdBy, MaxActorLength, nameof(createdBy));
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Primary key; also the <c>candidateId</c> the API returns and GET resolves.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack this preview belongs to (rack-scoped — part of the cache key, NFR2).</summary>
    public Guid RackId { get; private set; }

    /// <summary>The baseline desired-state version id this diff was computed against (part of the cache key).</summary>
    public Guid BaselineRevisionId { get; private set; }

    /// <summary>The SHA-256 of the candidate's canonical YAML (part of the cache key).</summary>
    public string CandidateSha256 { get; private set; }

    /// <summary>The SHA-256 of the baseline's canonical YAML (observability/audit).</summary>
    public string BaselineSha256 { get; private set; }

    /// <summary>The raw unified diff between baseline and candidate canonical YAML.</summary>
    public string RawUnifiedDiff { get; private set; }

    /// <summary>The structured semantic-change summary, serialized as jsonb.</summary>
    public string StructuredSummaryJson { get; private set; }

    /// <summary>When this preview was first computed and cached.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When this cache row expires and becomes eligible for pruning; <c>null</c> means never.</summary>
    public DateTime? ExpiresAtUtc { get; private set; }

    /// <summary>The actor (user or service subject) who first requested this preview.</summary>
    public string CreatedBy { get; private set; }

    private static string Bound(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value exceeds the {maxLength}-character bound.", paramName);
        }

        return value;
    }
}
