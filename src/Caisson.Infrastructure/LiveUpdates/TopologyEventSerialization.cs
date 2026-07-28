using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The single authoritative serializer for the live-updates wire format (story #9, ADR 0014). camelCase
/// property names and the polymorphic <c>type</c> discriminator are fixed here so the publisher, the
/// cross-instance relay, the SignalR payloads and any future client all agree byte-for-byte. Keep this
/// stable — it is a public contract documented in <c>docs/live-topology-events.md</c>.
/// </summary>
public static class TopologyEventSerialization
{
    /// <summary>The canonical <see cref="JsonSerializerOptions"/> for every live topology event.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes an event to its canonical polymorphic JSON envelope.</summary>
    public static string Serialize(TopologyEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return JsonSerializer.Serialize(@event, Options);
    }

    /// <summary>
    /// Deserializes a canonical envelope back to a concrete <see cref="TopologyEvent"/>, or null when the
    /// payload is not a recognised event (so a malformed channel message can never crash the relay).
    /// </summary>
    public static TopologyEvent? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TopologyEvent>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
