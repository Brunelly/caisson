using System.Text.Json;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Shaping;

namespace Caisson.Api.Contracts;

/// <summary>Maps domain/shaping types onto the API wire contracts. Pure and allocation-light.</summary>
public static class ContractMappers
{
    /// <summary>Maps snapshot metadata, parsing the change-count rollup when present.</summary>
    public static SnapshotMetadataDto ToMetadata(TopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SnapshotMetadataDto(
            snapshot.Id,
            snapshot.Version,
            snapshot.TriggerType.ToString(),
            snapshot.CreatedBy,
            snapshot.Source,
            snapshot.SourceVersion,
            snapshot.CreatedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.CorrelationId,
            snapshot.Status.ToString(),
            ParseOptional(snapshot.ChangeSummary?.ChangeCountsJson));
    }

    /// <summary>Maps a projected graph view onto the wire graph contract.</summary>
    public static TopologyGraphDto ToGraph(TopologyGraphView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TopologyGraphDto(
            view.SnapshotId,
            view.Version,
            view.CorrelationId,
            view.Servers.Select(ToServer).ToList(),
            view.UnmappedPorts.Select(p => new UnmappedPortDto(p.SwitchStableKey, p.SwitchSerial, p.PortName)).ToList());
    }

    /// <summary>Maps a stored per-entity diff row onto the wire diff contract.</summary>
    public static EntityDiffDto ToEntityDiff(TopologyEntityDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return new EntityDiffDto(
            diff.EntityType.ToString(),
            diff.EntityStableKey,
            diff.ChangeType.ToString(),
            Parse(diff.DiffPayloadJson),
            diff.PreviousSnapshotId,
            diff.SnapshotId,
            diff.CreatedAtUtc,
            diff.CorrelationId);
    }

    /// <summary>Maps an audit event onto the wire contract.</summary>
    public static AuditEventDto ToAudit(TopologyAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return new AuditEventDto(
            auditEvent.Id,
            auditEvent.RackId,
            auditEvent.SnapshotId,
            auditEvent.OccurredAtUtc,
            auditEvent.ActorType.ToString(),
            auditEvent.ActorId,
            auditEvent.Action,
            auditEvent.TargetType,
            auditEvent.TargetId,
            auditEvent.Result,
            auditEvent.CorrelationId);
    }

    private static ServerNodeDto ToServer(ServerNode server)
        => new(
            server.StableKey,
            server.Hostname,
            server.BmcUuid,
            server.Nics.Select(ToNic).ToList());

    private static NicNodeDto ToNic(NicNode nic)
        => new(
            nic.StableKey,
            nic.Name,
            nic.Mac,
            nic.BestAttachment is null ? null : ToAttachment(nic.BestAttachment),
            nic.Candidates.Select(ToAttachment).ToList());

    private static PortAttachmentDto ToAttachment(PortAttachment attachment)
        => new(
            attachment.SwitchStableKey,
            attachment.SwitchSerial,
            attachment.PortName,
            attachment.Confidence,
            attachment.Band,
            attachment.ReasonCode,
            attachment.Vlans);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement? ParseOptional(string? json)
        => string.IsNullOrEmpty(json) ? null : Parse(json);
}
