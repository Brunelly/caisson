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

    /// <summary>
    /// Maps a projected graph view onto the wire graph contract. Finding #29: <paramref name="isPrivileged"/>
    /// (Operator/Admin) gates whether NIC MACs are returned in full or OUI+masked — ReadOnly still gets a
    /// usable value for search/display, never the raw address.
    /// </summary>
    public static TopologyGraphDto ToGraph(TopologyGraphView view, bool isPrivileged)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TopologyGraphDto(
            view.SnapshotId,
            view.Version,
            view.CorrelationId,
            view.Servers.Select(s => ToServer(s, isPrivileged)).ToList(),
            view.UnmappedPorts.Select(p => new UnmappedPortDto(p.SwitchStableKey, p.SwitchSerial, p.PortName)).ToList(),
            view.Switches.Select(ToSwitchInventory).ToList());
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

    private static ServerNodeDto ToServer(ServerNode server, bool isPrivileged)
        => new(
            server.StableKey,
            server.Hostname,
            server.BmcUuid,
            server.Nics.Select(n => ToNic(n, isPrivileged)).ToList());

    private static NicNodeDto ToNic(NicNode nic, bool isPrivileged)
        => new(
            nic.StableKey,
            nic.Name,
            isPrivileged ? nic.Mac : RedactMac(nic.Mac),
            nic.BestAttachment is null ? null : ToAttachment(nic.BestAttachment),
            nic.Candidates.Select(ToAttachment).ToList(),
            nic.UnmappedReasonCode);

    /// <summary>
    /// Finding #29: masks a colon-grouped MAC's NIC-specific portion (the last three octets) while
    /// keeping the OUI (vendor) — enough for a ReadOnly caller's search/display to still group and
    /// recognise devices by manufacturer without exposing the individually-identifying full address.
    /// </summary>
    private static string RedactMac(string macDisplay)
    {
        var octets = macDisplay.Split(':');
        if (octets.Length != 6)
        {
            return "xx:xx:xx:xx:xx:xx";
        }

        return string.Join(':', octets[0], octets[1], octets[2], "xx", "xx", "xx");
    }

    private static SwitchInventoryDto ToSwitchInventory(SwitchInventoryNode @switch)
        => new(
            @switch.StableKey,
            @switch.Serial,
            @switch.Name,
            @switch.Ports.Select(p => new SwitchPortInventoryDto(p.StableKey, p.PortName)).ToList());

    private static PortAttachmentDto ToAttachment(PortAttachment attachment)
        => new(
            attachment.SwitchStableKey,
            attachment.SwitchSerial,
            attachment.PortName,
            attachment.Confidence,
            attachment.Band,
            attachment.ReasonCode,
            attachment.Vlans);

    /// <summary>
    /// Management-plane address field names redacted from the entity latest-fields dictionary for a
    /// non-privileged (ReadOnly) caller (finding #29): a switch's <c>managementIp</c>, a server's
    /// <c>bmcAddress</c>, and an LLDP neighbour's <c>mgmtAddress</c> — every field an operator could use
    /// to actually reach a device's management plane.
    /// </summary>
    private static readonly string[] ManagementAddressFieldNames = { "managementIp", "bmcAddress", "mgmtAddress" };

    /// <summary>
    /// Redacts management-plane address fields from an entity's latest-fields dictionary for a
    /// non-privileged caller (finding #29) — returns <paramref name="fields"/> unchanged for
    /// Operator/Admin, or a copy with the address fields nulled out for ReadOnly.
    /// </summary>
    public static IReadOnlyDictionary<string, string?>? RedactManagementFields(
        IReadOnlyDictionary<string, string?>? fields, bool isPrivileged)
    {
        if (fields is null || isPrivileged)
        {
            return fields;
        }

        var redacted = new Dictionary<string, string?>(fields, StringComparer.Ordinal);
        foreach (var name in ManagementAddressFieldNames)
        {
            if (redacted.ContainsKey(name))
            {
                redacted[name] = null;
            }
        }

        return redacted;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement? ParseOptional(string? json)
        => string.IsNullOrEmpty(json) ? null : Parse(json);
}
