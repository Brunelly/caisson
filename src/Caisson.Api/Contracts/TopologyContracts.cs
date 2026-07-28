using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caisson.Api.Contracts;

/// <summary>A single page of results with an opaque continuation cursor (null when exhausted).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Snapshot metadata (AC1/AC3). <see cref="DiffSummary"/> is the change-count rollup, if any.</summary>
public sealed record SnapshotMetadataDto(
    Guid SnapshotId,
    int Version,
    string TriggerType,
    string CreatedBy,
    string Source,
    string? SourceVersion,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    Guid CorrelationId,
    string Status,
    [property: JsonPropertyName("diffSummary")] JsonElement? DiffSummary);

/// <summary>A snapshot's metadata plus its projected topology graph (AC3 latest/detail).</summary>
public sealed record SnapshotDetailDto(SnapshotMetadataDto Snapshot, TopologyGraphDto Graph);

/// <summary>The projected topology graph for a snapshot (server-NIC → switch-port → VLAN).</summary>
public sealed record TopologyGraphDto(
    Guid SnapshotId,
    int Version,
    Guid CorrelationId,
    IReadOnlyList<ServerNodeDto> Servers,
    IReadOnlyList<UnmappedPortDto> UnmappedPorts);

/// <summary>A server and its NICs in the graph.</summary>
public sealed record ServerNodeDto(
    string StableKey,
    string? Hostname,
    string? BmcUuid,
    IReadOnlyList<NicNodeDto> Nics);

/// <summary>A NIC with its best attachment and all candidate attachments.</summary>
public sealed record NicNodeDto(
    string StableKey,
    string Name,
    string Mac,
    PortAttachmentDto? BestAttachment,
    IReadOnlyList<PortAttachmentDto> Candidates);

/// <summary>A candidate NIC-to-port attachment with confidence, band, reason and VLANs.</summary>
public sealed record PortAttachmentDto(
    string SwitchStableKey,
    string? SwitchSerial,
    string PortName,
    double Confidence,
    string Band,
    string ReasonCode,
    IReadOnlyList<int> Vlans);

/// <summary>A switch port that no NIC mapped to.</summary>
public sealed record UnmappedPortDto(string SwitchStableKey, string? SwitchSerial, string PortName);

/// <summary>A single per-entity diff (AC2 stored history or AC3 live drift).</summary>
public sealed record EntityDiffDto(
    string EntityType,
    string EntityStableKey,
    string ChangeType,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    Guid? PreviousSnapshotId,
    Guid? ToSnapshotId,
    DateTime? CreatedAt,
    Guid CorrelationId);

/// <summary>The drift between two snapshots computed live (AC3 diff endpoint).</summary>
public sealed record SnapshotDiffDto(
    Guid FromSnapshotId,
    Guid ToSnapshotId,
    [property: JsonPropertyName("changeSummary")] JsonElement ChangeSummary,
    IReadOnlyList<EntityDiffDto> Diffs);

/// <summary>An entity's current representation (if it still exists) plus its stored change history.</summary>
public sealed record EntityDetailDto(
    string EntityType,
    string StableKey,
    IReadOnlyDictionary<string, string?>? Latest,
    IReadOnlyList<EntityDiffDto> History);

/// <summary>An audit-trail event (AC3).</summary>
public sealed record AuditEventDto(
    Guid AuditEventId,
    Guid? RackId,
    Guid? SnapshotId,
    DateTime OccurredAt,
    string ActorType,
    string ActorId,
    string Action,
    string TargetType,
    string? TargetId,
    string Result,
    Guid CorrelationId);
