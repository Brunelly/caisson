using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.NetworkConfig.Preflight;

namespace Caisson.Api.Contracts;

/// <summary>The impact-preview compute request (story #171, AC1): the candidate YAML to diff against the baseline.</summary>
public sealed record ImpactPreviewRequest(string? Yaml);

/// <summary>One field snapshot within a change's before/after state, on the wire.</summary>
public sealed record ImpactChangeFieldDto(string Field, string? Value);

/// <summary>
/// One semantic change on the wire (story #171, AC1/AC3). Mirrors <see cref="DesiredStateChange"/> plus the
/// <see cref="ExistsInTopology"/> annotation the API adds so the UI renders a topology deep link (true) or a
/// non-blocking "not found in topology" badge (false). Reuses <see cref="EntityRefDto"/>.
/// </summary>
public sealed record ImpactChangeDto(
    string Kind,
    string Category,
    Guid ChangeId,
    string Summary,
    EntityRefDto EntityRef,
    bool ExistsInTopology,
    IReadOnlyList<ImpactChangeFieldDto> Before,
    IReadOnlyList<ImpactChangeFieldDto> After);

/// <summary>
/// The impact-preview response (story #171, AC1/AC2/AC3). Carries the cache identity (<see cref="CandidateId"/>
/// = the cache row id) and timestamp for observability/audit, the baseline revision + commit, the
/// <see cref="CacheHit"/> flag, the raw unified diff, and the structured summary grouped by VLANs / ports.
/// </summary>
public sealed record ImpactPreviewResponse(
    Guid CandidateId,
    string CandidateSha256,
    string BaselineSha256,
    Guid BaselineRevisionId,
    string? BaselineCommitSha,
    bool CacheHit,
    DateTime CreatedAtUtc,
    string RawUnifiedDiff,
    IReadOnlyList<ImpactChangeDto> VlanChanges,
    IReadOnlyList<ImpactChangeDto> PortChanges);

/// <summary>The 409 body when the rack has no ingested baseline revision (story #171, AC5).</summary>
public sealed record MissingBaselineResponse(string ReasonCode, string Message);

/// <summary>
/// The persisted structured-summary payload stored in the diff cache's <c>structured_summary_json</c>
/// column. Storing the baseline commit and the fully-annotated change list means a cache hit reconstructs
/// the whole response from the row alone — byte-identical to the first computed response (AC2).
/// </summary>
public sealed record ImpactPreviewSummaryPayload(
    string? BaselineCommitSha,
    IReadOnlyList<StoredImpactChange> Changes);

/// <summary>One stored change (jsonb). Mirrors <see cref="ImpactChangeDto"/> including the topology annotation.</summary>
public sealed record StoredImpactChange(
    string Kind,
    string Category,
    Guid ChangeId,
    string Summary,
    StoredEntityRef EntityRef,
    bool ExistsInTopology,
    IReadOnlyList<StoredImpactField> Before,
    IReadOnlyList<StoredImpactField> After);

/// <summary>One stored before/after field (jsonb).</summary>
public sealed record StoredImpactField(string Field, string? Value);

/// <summary>The stored entity reference (jsonb), mirroring <see cref="EntityRef"/>.</summary>
public sealed record StoredEntityRef(
    string Kind,
    Guid RackId,
    string? SwitchStableKey,
    string? PortName,
    int? VlanId);

/// <summary>
/// Serializes/deserializes the impact-preview structured summary and maps between the domain change model,
/// the stored jsonb payload, and the wire response. The stored payload is the single source of truth on a
/// cache hit, so the compute path serializes exactly what the hit path deserializes (AC2).
/// </summary>
public static class ImpactPreviewContractMappers
{
    /// <summary>The canonical, deterministic serializer for the stored structured-summary payload.</summary>
    public static JsonSerializerOptions SummaryJsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes a computed summary (baseline commit + annotated changes) to the stored jsonb string.</summary>
    public static string SerializeSummary(ImpactPreviewSummaryPayload payload)
        => JsonSerializer.Serialize(payload, SummaryJsonOptions);

    /// <summary>Deserializes the stored jsonb summary; throws if the stored payload is malformed (should never happen).</summary>
    public static ImpactPreviewSummaryPayload DeserializeSummary(string json)
        => JsonSerializer.Deserialize<ImpactPreviewSummaryPayload>(json, SummaryJsonOptions)
            ?? throw new JsonException("The stored impact-preview summary deserialized to null.");

    /// <summary>Builds the stored payload from the pure domain changes plus the per-change topology annotation.</summary>
    public static ImpactPreviewSummaryPayload ToStoredPayload(
        string? baselineCommitSha,
        IReadOnlyList<DesiredStateChange> changes,
        Func<DesiredStateChange, bool> existsInTopology)
    {
        var stored = changes
            .Select(c => new StoredImpactChange(
                c.Kind.ToString(),
                c.Category.ToString(),
                c.ChangeId,
                c.Summary,
                new StoredEntityRef(
                    c.EntityRef.Kind.ToString().ToLowerInvariant(),
                    c.EntityRef.RackId,
                    c.EntityRef.SwitchStableKey,
                    c.EntityRef.PortName,
                    c.EntityRef.VlanId),
                existsInTopology(c),
                c.Before.Select(f => new StoredImpactField(f.Field, f.Value)).ToList(),
                c.After.Select(f => new StoredImpactField(f.Field, f.Value)).ToList()))
            .ToList();
        return new ImpactPreviewSummaryPayload(baselineCommitSha, stored);
    }

    /// <summary>Builds the wire response from the cache row plus its deserialized stored summary.</summary>
    public static ImpactPreviewResponse ToResponse(DesiredStateCandidateDiffCache row, bool cacheHit)
    {
        ArgumentNullException.ThrowIfNull(row);

        var summary = DeserializeSummary(row.StructuredSummaryJson);
        var changes = summary.Changes.Select(ToDto).ToList();

        return new ImpactPreviewResponse(
            row.Id,
            row.CandidateSha256,
            row.BaselineSha256,
            row.BaselineRevisionId,
            summary.BaselineCommitSha,
            cacheHit,
            row.CreatedAtUtc,
            row.RawUnifiedDiff,
            changes.Where(c => string.Equals(c.Category, nameof(DesiredStateChangeCategory.Vlan), StringComparison.Ordinal)).ToList(),
            changes.Where(c => string.Equals(c.Category, nameof(DesiredStateChangeCategory.Port), StringComparison.Ordinal)).ToList());
    }

    private static ImpactChangeDto ToDto(StoredImpactChange change)
        => new(
            change.Kind,
            change.Category,
            change.ChangeId,
            change.Summary,
            new EntityRefDto(
                change.EntityRef.Kind,
                change.EntityRef.RackId,
                change.EntityRef.SwitchStableKey,
                change.EntityRef.PortName,
                change.EntityRef.VlanId),
            change.ExistsInTopology,
            change.Before.Select(f => new ImpactChangeFieldDto(f.Field, f.Value)).ToList(),
            change.After.Select(f => new ImpactChangeFieldDto(f.Field, f.Value)).ToList());
}
