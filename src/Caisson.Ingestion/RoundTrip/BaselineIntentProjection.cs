using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.Materializer;

namespace Caisson.Ingestion.RoundTrip;

/// <summary>
/// Projects a persisted baseline desired-state revision's materialized JSON into the round-trip
/// <see cref="SupportedDesiredStateModel"/> so the baseline and a candidate can be diffed symmetrically
/// (story #171, AC1). The baseline is the JSON written by <c>DesiredStateIngestionService</c> via
/// <see cref="DesiredStatePayloadSerializer"/> — a <c>ValidatedRackDocument</c> shape
/// (<c>{ rackSlug, switches[].ports[] }</c>). Each port's <c>accessVlan</c> becomes a
/// <see cref="PortAccessIntent"/>; the <see cref="SupportedDesiredStateModel.RackSlug"/> is set to the
/// server-authoritative slug the caller resolved (so it matches the candidate and the raw diff carries no
/// slug noise).
/// <para>
/// Scope note (ADR 0053): the M1 ingestion schema does NOT persist a VLAN catalogue
/// (<c>spec.vlans</c>) — <c>ValidatedRackDocument</c> carries only <c>rackSlug</c> + <c>switches</c> — so
/// the projected baseline <see cref="SupportedDesiredStateModel.VlanCatalogue"/> is empty. VLAN catalogue
/// differences therefore surface as additions against an ingested baseline until ingestion models the
/// catalogue.
/// </para>
/// </summary>
public static class BaselineIntentProjection
{
    /// <summary>
    /// Projects <paramref name="desiredStateJson"/> (the persisted baseline payload) into a supported model
    /// keyed on the server-authoritative <paramref name="rackSlug"/>.
    /// </summary>
    /// <exception cref="JsonException">Thrown if the persisted payload is not the expected shape (should never happen).</exception>
    public static SupportedDesiredStateModel Project(string rackSlug, string desiredStateJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(desiredStateJson);

        var document = JsonSerializer.Deserialize<BaselineDocument>(desiredStateJson, DesiredStatePayloadSerializer.Options)
            ?? throw new JsonException("The persisted baseline desired-state payload deserialized to null.");

        var portIntents = new List<PortAccessIntent>();
        foreach (var @switch in document.Switches ?? Array.Empty<BaselineSwitch>())
        {
            if (string.IsNullOrEmpty(@switch.Name))
            {
                continue;
            }

            foreach (var port in @switch.Ports ?? Array.Empty<BaselinePort>())
            {
                if (string.IsNullOrEmpty(port.Name))
                {
                    continue;
                }

                portIntents.Add(new PortAccessIntent(@switch.Name, port.Name, port.AccessVlan));
            }
        }

        return new SupportedDesiredStateModel(rackSlug, Array.Empty<VlanCatalogueEntry>(), portIntents);
    }

    private sealed record BaselineDocument(
        [property: JsonPropertyName("rackSlug")] string? RackSlug,
        [property: JsonPropertyName("switches")] IReadOnlyList<BaselineSwitch>? Switches);

    private sealed record BaselineSwitch(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("ports")] IReadOnlyList<BaselinePort>? Ports);

    private sealed record BaselinePort(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("accessVlan")] int AccessVlan);
}
