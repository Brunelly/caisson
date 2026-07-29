using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Ingestion.Schema;

namespace Caisson.Ingestion.Materializer;

/// <summary>
/// The single authoritative serializer for the persisted desired-state revision payload (story #63,
/// AC1/AC2), modelled directly on
/// <see cref="Caisson.Infrastructure.LiveUpdates.TopologyEventSerialization"/>: one fixed
/// <see cref="JsonSerializerOptions"/> so the same validated document always serializes to the same
/// bytes. Determinism matters here because the persisted content hash and the stored payload must be
/// stable and comparable across ingestion runs — property order follows
/// <see cref="ValidatedRackDocument"/>'s fixed declaration order and
/// <see cref="ValidatedSwitch.Ports"/>/<see cref="ValidatedRackDocument.Switches"/> preserve the source
/// YAML sequence order the validator already produced, so no extra sort step is needed.
/// </summary>
public static class DesiredStatePayloadSerializer
{
    /// <summary>The canonical <see cref="JsonSerializerOptions"/> for the desired-state payload.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes a validated rack document to its canonical, deterministic JSON snapshot.</summary>
    public static string Serialize(ValidatedRackDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }
}
