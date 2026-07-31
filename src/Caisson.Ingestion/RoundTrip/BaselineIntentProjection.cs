using System.Globalization;
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
/// (<c>spec.vlans</c>) — <c>ValidatedRackDocument</c> carries only <c>rackSlug</c> + <c>switches</c>. So the
/// baseline VLAN catalogue is <b>synthesized</b> from the distinct access-VLAN ids the baseline's ports
/// reference: this keeps the projected model internally consistent (every port references a catalogue VLAN,
/// so the shared <c>DesiredStateYamlRenderer</c>/<c>NetworkIntentValidator</c> can render it). Baseline VLAN
/// names are unknown to ingestion, so they are taken from the candidate's catalogue as a naming hint for the
/// same id (avoiding a false "name changed" diff for an unchanged VLAN); a VLAN id the candidate does not
/// carry falls back to a synthetic <c>vlan-{id}</c> name.
/// </para>
/// <para>
/// Runtime consequence (ADR 0053): because a VLAN present in both baseline and candidate takes its baseline
/// name from the candidate hint, <c>baseline.Name == candidate.Name</c> by construction, so the
/// <see cref="Diffing.SemanticDiffEngine"/>'s VLAN name/description "Modified" branch is unreachable in the
/// M1 pipeline (AC1's <c>VLAN 20 name changed 'corp'→'prod'</c> example cannot be produced end-to-end until
/// ingestion persists a VLAN catalogue). VLAN add/remove and per-port access-VLAN changes are fully
/// reachable.
/// </para>
/// </summary>
public static class BaselineIntentProjection
{
    /// <summary>
    /// Projects <paramref name="desiredStateJson"/> (the persisted baseline payload) into a supported model
    /// keyed on the server-authoritative <paramref name="rackSlug"/>. <paramref name="candidateVlanHint"/>
    /// supplies names for baseline VLAN ids the candidate also carries, so an unchanged VLAN does not diff
    /// as a spurious name change.
    /// </summary>
    /// <exception cref="JsonException">Thrown if the persisted payload is not the expected shape (should never happen).</exception>
    public static SupportedDesiredStateModel Project(
        string rackSlug, string desiredStateJson, IReadOnlyList<VlanCatalogueEntry>? candidateVlanHint = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(desiredStateJson);

        var document = JsonSerializer.Deserialize<BaselineDocument>(desiredStateJson, DesiredStatePayloadSerializer.Options)
            ?? throw new JsonException("The persisted baseline desired-state payload deserialized to null.");

        var hintById = new Dictionary<int, VlanCatalogueEntry>();
        foreach (var vlan in candidateVlanHint ?? Array.Empty<VlanCatalogueEntry>())
        {
            hintById.TryAdd(vlan.Id, vlan);
        }

        var portIntents = new List<PortAccessIntent>();
        var vlanIds = new SortedSet<int>();
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
                vlanIds.Add(port.AccessVlan);
            }
        }

        var vlanCatalogue = vlanIds
            .Select(id => hintById.TryGetValue(id, out var hint)
                ? hint
                : new VlanCatalogueEntry(id, "vlan-" + id.ToString(CultureInfo.InvariantCulture), null))
            .ToList();

        return new SupportedDesiredStateModel(rackSlug, vlanCatalogue, portIntents);
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
