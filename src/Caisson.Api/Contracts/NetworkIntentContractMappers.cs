using System.Text.Json;
using Caisson.Domain.NetworkConfig;

namespace Caisson.Api.Contracts;

/// <summary>
/// Maps between the wire contracts, the domain payload records, and <see cref="RackNetworkIntent"/>'s
/// serialized <c>IntentJson</c> column. The only place that (de)serializes that JSON — the PUT save path
/// and the GET read path both go through here, so the wire shape and the stored shape can never drift.
/// </summary>
public static class NetworkIntentContractMappers
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maps a save/validate request onto the domain payload records the validator consumes.</summary>
    public static (IReadOnlyList<VlanCatalogueEntry> VlanCatalogue, IReadOnlyList<PortAccessIntent> PortIntents)
        FromRequest(NetworkIntentSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vlanCatalogue = (request.VlanCatalogue ?? Array.Empty<VlanCatalogueEntryDto>())
            .Select(v => new VlanCatalogueEntry(v.Id, v.Name, v.Description))
            .ToList();
        var portIntents = (request.PortIntents ?? Array.Empty<PortAccessIntentDto>())
            .Select(p => new PortAccessIntent(p.SwitchStableKey, p.PortName, p.AccessVlanId))
            .ToList();
        return (vlanCatalogue, portIntents);
    }

    /// <summary>Serializes a validated payload into the entity's bounded <c>IntentJson</c> column value.</summary>
    public static string ToIntentJson(
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue, IReadOnlyList<PortAccessIntent> portIntents)
        => JsonSerializer.Serialize(new IntentPayload(vlanCatalogue, portIntents), SerializerOptions);

    /// <summary>Maps a persisted entity onto the GET/PUT response DTO.</summary>
    public static NetworkIntentDto ToDto(RackNetworkIntent entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var payload = DeserializePayload(entity.IntentJson);
        return new NetworkIntentDto(
            entity.RackId,
            payload.VlanCatalogue.Select(ToDto).ToList(),
            payload.PortIntents.Select(ToDto).ToList(),
            entity.UpdatedAtUtc,
            entity.UpdatedBy);
    }

    /// <summary>The GET response shape for a rack with no saved network intent yet (AC1: viewable, not 404).</summary>
    public static NetworkIntentDto ToEmptyDto(Guid rackId)
        => new(rackId, Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>(), null, null);

    private static IntentPayload DeserializePayload(string intentJson)
        => JsonSerializer.Deserialize<IntentPayload>(intentJson, SerializerOptions)
            ?? new IntentPayload(Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());

    private static VlanCatalogueEntryDto ToDto(VlanCatalogueEntry entry) => new(entry.Id, entry.Name, entry.Description);

    private static PortAccessIntentDto ToDto(PortAccessIntent intent)
        => new(intent.SwitchStableKey, intent.PortName, intent.AccessVlanId);

    private sealed record IntentPayload(
        IReadOnlyList<VlanCatalogueEntry> VlanCatalogue, IReadOnlyList<PortAccessIntent> PortIntents);
}
