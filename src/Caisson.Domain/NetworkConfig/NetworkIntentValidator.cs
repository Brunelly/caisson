using Caisson.Domain.DesiredState;

namespace Caisson.Domain.NetworkConfig;

/// <summary>
/// The single, shared network-intent validation ruleset (story #168, NFR5: "no duplicated validation
/// logic across screens"). Pure and EF-free so it can run identically from the PUT save endpoint and the
/// <c>/network-intent/validate</c> stub (story #176) without either duplicating or drifting from the
/// other. Reuses <see cref="DesiredStateSchema"/>'s VLAN-range/description-length bounds rather than
/// redefining them, so authoring and the future #169 YAML generation pipeline can never disagree about
/// what a valid VLAN id or description looks like.
/// </summary>
public static class NetworkIntentValidator
{
    /// <summary>
    /// Maximum length of a VLAN catalogue entry's <see cref="VlanCatalogueEntry.Name"/> — reuses
    /// <see cref="DesiredStateSchema.MaxSwitchNameLength"/>, the schema's existing "short device/label
    /// name" bound, rather than inventing a parallel constant.
    /// </summary>
    public const int MaxVlanNameLength = DesiredStateSchema.MaxSwitchNameLength;

    /// <summary>
    /// Validates a rack's full authored network-intent payload (AC1/AC2), accumulating every problem
    /// found rather than failing on the first one (mirrors <c>DesiredStateValidator</c>'s style). The
    /// story's "block deletion of a VLAN still referenced by a port intent" rule (Q2 answer) falls out of
    /// the same "port intent VLAN must exist in the catalogue" check below: because the PUT payload
    /// always carries the FULL catalogue plus ALL port intents, removing a still-referenced VLAN from
    /// <paramref name="vlanCatalogue"/> makes the referencing entry in <paramref name="portIntents"/> fail
    /// validation — there is no separate "is this VLAN referenced elsewhere" pass to keep in sync.
    /// </summary>
    public static IReadOnlyList<(string Field, string Message)> Validate(
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue,
        IReadOnlyList<PortAccessIntent> portIntents)
    {
        ArgumentNullException.ThrowIfNull(vlanCatalogue);
        ArgumentNullException.ThrowIfNull(portIntents);

        var errors = new List<(string Field, string Message)>();
        var catalogueIds = new HashSet<int>();

        for (var i = 0; i < vlanCatalogue.Count; i++)
        {
            var entry = vlanCatalogue[i];
            var idField = $"vlanCatalogue[{i}].id";
            var nameField = $"vlanCatalogue[{i}].name";
            var descriptionField = $"vlanCatalogue[{i}].description";

            if (entry.Id < DesiredStateSchema.MinVlan || entry.Id > DesiredStateSchema.MaxVlan)
            {
                errors.Add((idField,
                    $"VLAN ID {entry.Id} is out of range [{DesiredStateSchema.MinVlan}, {DesiredStateSchema.MaxVlan}]."));
            }
            else if (!catalogueIds.Add(entry.Id))
            {
                errors.Add((idField, $"VLAN ID {entry.Id} already exists in this rack."));
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                errors.Add((nameField, "VLAN name is required."));
            }
            else if (entry.Name.Length > MaxVlanNameLength)
            {
                errors.Add((nameField, $"VLAN name exceeds the {MaxVlanNameLength}-character bound."));
            }

            if (entry.Description is { Length: > 0 } description
                && description.Length > DesiredStateSchema.MaxDescriptionLength)
            {
                errors.Add((descriptionField,
                    $"Description exceeds the {DesiredStateSchema.MaxDescriptionLength}-character bound."));
            }
        }

        for (var i = 0; i < portIntents.Count; i++)
        {
            var intent = portIntents[i];
            var switchField = $"portIntents[{i}].switchStableKey";
            var portField = $"portIntents[{i}].portName";
            var vlanField = $"portIntents[{i}].accessVlanId";

            if (string.IsNullOrWhiteSpace(intent.SwitchStableKey))
            {
                errors.Add((switchField, "switchStableKey is required."));
            }

            if (string.IsNullOrWhiteSpace(intent.PortName))
            {
                errors.Add((portField, "portName is required."));
            }

            if (intent.AccessVlanId is { } vlanId && !catalogueIds.Contains(vlanId))
            {
                errors.Add((vlanField,
                    $"VLAN {vlanId} does not exist in this rack's VLAN catalogue."));
            }
        }

        return errors;
    }
}
