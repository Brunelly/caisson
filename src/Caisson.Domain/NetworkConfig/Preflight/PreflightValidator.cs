using System.Globalization;
using System.Text.RegularExpressions;
using Caisson.Domain.DesiredState;

namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// The pure, EF-free pre-flight validation engine (story #170). An ordered, deterministic pipeline over an
/// authored candidate: (a) schema + intra-payload semantic checks, delegated VERBATIM to the shared
/// <see cref="NetworkIntentValidator"/> (no second rule set) and translated onto field-addressable
/// <see cref="PreflightIssue"/>s via one mapping table; (b) topology semantic resolution of each port
/// intent against the observed <see cref="RackInventory"/>; and (c) non-blocking safety guardrails for
/// changes to management/uplink ports, run ONLY when no blocking errors exist. Issues are normalized into a
/// deterministic order (catalogue order, then port-intent order, then code) so the issue set, field paths
/// and (via <see cref="ValidationRunToken"/>) the validationRunId are stable across re-runs (NFR3, AC4).
/// </summary>
public static class PreflightValidator
{
    private static readonly Regex FieldPattern = new(
        @"^(vlanCatalogue|portIntents)\[(\d+)\]\.(\w+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int RackGroup = 0;
    private const int VlanGroup = 1;
    private const int PortGroup = 2;

    /// <summary>Runs the full schema → semantic → safety pipeline, returning the sorted, deterministic issue set.</summary>
    public static IReadOnlyList<PreflightIssue> Validate(
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue,
        IReadOnlyList<PortAccessIntent> portIntents,
        RackInventory inventory,
        Guid rackId)
    {
        ArgumentNullException.ThrowIfNull(vlanCatalogue);
        ArgumentNullException.ThrowIfNull(portIntents);
        ArgumentNullException.ThrowIfNull(inventory);

        var issues = new List<(SortKey Key, PreflightIssue Issue)>();

        // (a) Schema + intra-payload semantic stage — reuse the shared ruleset verbatim, then translate.
        foreach (var (field, message) in NetworkIntentValidator.Validate(vlanCatalogue, portIntents))
        {
            issues.Add(TranslateValidatorIssue(field, message, vlanCatalogue, portIntents, rackId));
        }

        // (b) Topology semantic stage — resolve every fully-formed port intent against observed inventory.
        AddTopologyIssues(issues, portIntents, inventory, rackId);

        // (c) Safety stage — only when nothing above blocks. Warn on changes to management/uplink ports.
        var hasBlockingError = issues.Any(i => i.Issue.Severity == PreflightSeverity.Error);
        if (!hasBlockingError)
        {
            AddSafetyIssues(issues, portIntents, inventory, rackId);
        }

        return issues
            .OrderBy(i => i.Key.Group)
            .ThenBy(i => i.Key.Index)
            .ThenBy(i => i.Key.Code, StringComparer.Ordinal)
            .ThenBy(i => i.Key.FieldPath, StringComparer.Ordinal)
            .Select(i => i.Issue)
            .ToList();
    }

    /// <summary>
    /// Translates one shared-validator <c>(field, message)</c> onto a field-addressable issue. Codes are
    /// derived structurally from the same authored data the validator read (never by parsing its message),
    /// so schema-vs-semantic classification stays robust and never drifts from the validator's wording.
    /// </summary>
    private static (SortKey Key, PreflightIssue Issue) TranslateValidatorIssue(
        string field, string message,
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue,
        IReadOnlyList<PortAccessIntent> portIntents,
        Guid rackId)
    {
        var match = FieldPattern.Match(field);
        if (!match.Success)
        {
            var pointer = "/";
            return (new SortKey(RackGroup, 0, PreflightCodes.SchemaInvalid, pointer),
                new PreflightIssue(
                    PreflightSeverity.Error, PreflightCodes.SchemaInvalid, message, pointer, null,
                    EntityRef.Rack(rackId)));
        }

        var collection = match.Groups[1].Value;
        var index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var suffix = match.Groups[3].Value;

        return collection == "vlanCatalogue"
            ? TranslateVlanIssue(index, suffix, message, vlanCatalogue, rackId)
            : TranslatePortIssue(index, suffix, message, portIntents, rackId);
    }

    private static (SortKey Key, PreflightIssue Issue) TranslateVlanIssue(
        int index, string suffix, string message, IReadOnlyList<VlanCatalogueEntry> vlanCatalogue, Guid rackId)
    {
        var entry = index >= 0 && index < vlanCatalogue.Count ? vlanCatalogue[index] : null;
        var (code, property) = suffix switch
        {
            "id" => (InRange(entry) ? PreflightCodes.DuplicateVlanId : PreflightCodes.VlanIdRange, "id"),
            "name" => (string.IsNullOrWhiteSpace(entry?.Name)
                ? PreflightCodes.VlanNameRequired
                : PreflightCodes.VlanNameLength, "name"),
            "description" => (PreflightCodes.VlanDescriptionLength, "description"),
            _ => (PreflightCodes.SchemaInvalid, suffix),
        };

        var fieldPath = JsonPointer.Build("vlanCatalogue", index, property);
        var uiPath = $"vlanCatalogue.vlans[{index.ToString(CultureInfo.InvariantCulture)}].{property}";
        var entityRef = EntityRef.Vlan(rackId, entry?.Id ?? 0);
        return (new SortKey(VlanGroup, index, code, fieldPath),
            new PreflightIssue(PreflightSeverity.Error, code, message, fieldPath, uiPath, entityRef));
    }

    private static (SortKey Key, PreflightIssue Issue) TranslatePortIssue(
        int index, string suffix, string message, IReadOnlyList<PortAccessIntent> portIntents, Guid rackId)
    {
        var intent = index >= 0 && index < portIntents.Count ? portIntents[index] : null;
        var switchKey = intent?.SwitchStableKey ?? string.Empty;
        var portName = intent?.PortName ?? string.Empty;

        string code;
        string property;
        var issueMessage = message;

        switch (suffix)
        {
            case "switchStableKey":
                code = PreflightCodes.SwitchKeyRequired;
                property = "switchStableKey";
                break;
            case "accessVlanId":
                code = PreflightCodes.VlanNotInCatalogue;
                property = "accessVlanId";
                break;
            case "portName" when string.IsNullOrWhiteSpace(portName):
                code = PreflightCodes.PortNameRequired;
                property = "portName";
                break;
            case "portName":
                // A duplicate (switch,port) — distinguish an identical duplicate from a genuine
                // access-VLAN conflict (AC2), mapping a conflict onto the assignment field.
                var earlier = FirstEarlierDuplicate(portIntents, index);
                if (earlier is { } prior && prior.AccessVlanId != intent?.AccessVlanId)
                {
                    code = PreflightCodes.PortVlanConflict;
                    property = "accessVlanId";
                    issueMessage =
                        $"Port '{portName}' on switch '{switchKey}' is assigned conflicting access VLANs " +
                        $"({Describe(prior.AccessVlanId)} and {Describe(intent?.AccessVlanId)}) in this candidate.";
                }
                else
                {
                    code = PreflightCodes.DuplicatePortIntent;
                    property = "portName";
                }

                break;
            default:
                code = PreflightCodes.SchemaInvalid;
                property = suffix;
                break;
        }

        var fieldPath = JsonPointer.Build("portIntents", index, property);
        var uiPath = PortUiPath(switchKey, portName, property);
        var entityRef = EntityRef.Port(rackId, switchKey, portName);
        return (new SortKey(PortGroup, index, code, fieldPath),
            new PreflightIssue(PreflightSeverity.Error, code, issueMessage, fieldPath, uiPath, entityRef));
    }

    private static void AddTopologyIssues(
        List<(SortKey Key, PreflightIssue Issue)> issues,
        IReadOnlyList<PortAccessIntent> portIntents,
        RackInventory inventory,
        Guid rackId)
    {
        var reportedTopologyUnavailable = false;

        for (var i = 0; i < portIntents.Count; i++)
        {
            var intent = portIntents[i];
            if (string.IsNullOrWhiteSpace(intent.SwitchStableKey) || string.IsNullOrWhiteSpace(intent.PortName))
            {
                // A missing switch/port is already a schema error; skip resolution to avoid duplicate noise.
                continue;
            }

            if (!inventory.HasSnapshot)
            {
                if (!reportedTopologyUnavailable)
                {
                    reportedTopologyUnavailable = true;
                    const string fieldPath = "/portIntents";
                    issues.Add((new SortKey(RackGroup, 0, PreflightCodes.TopologyUnavailable, fieldPath),
                        new PreflightIssue(
                            PreflightSeverity.Error, PreflightCodes.TopologyUnavailable,
                            "No topology snapshot is available for this rack, so port intents cannot be " +
                            "resolved. Refresh topology discovery, then validate again.",
                            fieldPath, "ports", EntityRef.Rack(rackId))));
                }

                continue;
            }

            var resolvedSwitch = inventory.FindSwitch(intent.SwitchStableKey);
            if (resolvedSwitch is null)
            {
                var fieldPath = JsonPointer.Build("portIntents", i, "switchStableKey");
                issues.Add((new SortKey(PortGroup, i, PreflightCodes.SwitchNotFound, fieldPath),
                    new PreflightIssue(
                        PreflightSeverity.Error, PreflightCodes.SwitchNotFound,
                        $"Switch '{intent.SwitchStableKey}' is not present in the current rack topology. " +
                        "Select a known switch or refresh topology discovery.",
                        fieldPath, PortUiPath(intent.SwitchStableKey, intent.PortName, "switchStableKey"),
                        EntityRef.Switch(rackId, intent.SwitchStableKey))));
                continue;
            }

            if (resolvedSwitch.FindPort(intent.PortName) is null)
            {
                var fieldPath = JsonPointer.Build("portIntents", i, "portName");
                issues.Add((new SortKey(PortGroup, i, PreflightCodes.PortNotFound, fieldPath),
                    new PreflightIssue(
                        PreflightSeverity.Error, PreflightCodes.PortNotFound,
                        $"Port '{intent.PortName}' was not found on switch '{intent.SwitchStableKey}'. " +
                        "Select a known port or refresh topology discovery.",
                        fieldPath, PortUiPath(intent.SwitchStableKey, intent.PortName, "portName"),
                        EntityRef.Port(rackId, intent.SwitchStableKey, intent.PortName))));
            }
        }
    }

    private static void AddSafetyIssues(
        List<(SortKey Key, PreflightIssue Issue)> issues,
        IReadOnlyList<PortAccessIntent> portIntents,
        RackInventory inventory,
        Guid rackId)
    {
        for (var i = 0; i < portIntents.Count; i++)
        {
            var intent = portIntents[i];
            if (intent.AccessVlanId is not { } vlan
                || string.IsNullOrWhiteSpace(intent.SwitchStableKey)
                || string.IsNullOrWhiteSpace(intent.PortName))
            {
                continue;
            }

            var port = inventory.FindSwitch(intent.SwitchStableKey)?.FindPort(intent.PortName);
            if (port is null || (port.Role != PortRole.Uplink && port.Role != PortRole.Management))
            {
                continue;
            }

            // Only a genuine CHANGE warrants a warning — an intent that matches the observed native VLAN
            // (or leaves the port Unchanged/Inherit) targets nothing (AC3 "no changes ⇒ no warnings").
            if (vlan == port.Pvid)
            {
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(port.RoleReason) ? "heuristic-derived" : port.RoleReason!;
            var (code, message) = port.Role == PortRole.Uplink
                ? (PreflightCodes.UplinkPort,
                    $"Port '{intent.PortName}' on switch '{intent.SwitchStableKey}' is classified as an " +
                    $"uplink ({reason}). Changing its access VLAN to {vlan} may disrupt inter-switch connectivity.")
                : (PreflightCodes.ManagementPort,
                    $"Port '{intent.PortName}' on switch '{intent.SwitchStableKey}' is classified as a " +
                    $"management port ({reason}). Changing its access VLAN to {vlan} may sever the management path to this rack.");

            var fieldPath = JsonPointer.Build("portIntents", i, "accessVlanId");
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "heuristic-derived",
                ["classification"] = port.Role == PortRole.Uplink ? "uplink" : "management",
                ["portRole"] = reason,
            };
            issues.Add((new SortKey(PortGroup, i, code, fieldPath),
                new PreflightIssue(
                    PreflightSeverity.Warning, code, message, fieldPath,
                    PortUiPath(intent.SwitchStableKey, intent.PortName, "accessVlanId"),
                    EntityRef.Port(rackId, intent.SwitchStableKey, intent.PortName),
                    Details: details)));
        }
    }

    private static string PortUiPath(string switchKey, string portName, string property)
        => $"ports[\"{switchKey}/{portName}\"].{property}";

    private static bool InRange(VlanCatalogueEntry? entry)
        => entry is { } e && e.Id >= DesiredStateSchema.MinVlan && e.Id <= DesiredStateSchema.MaxVlan;

    private static PortAccessIntent? FirstEarlierDuplicate(IReadOnlyList<PortAccessIntent> portIntents, int index)
    {
        if (index < 0 || index >= portIntents.Count)
        {
            return null;
        }

        var target = portIntents[index];
        for (var j = 0; j < index; j++)
        {
            var candidate = portIntents[j];
            if (string.Equals(candidate.SwitchStableKey, target.SwitchStableKey, StringComparison.Ordinal)
                && string.Equals(candidate.PortName, target.PortName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Describe(int? vlan)
        => vlan?.ToString(CultureInfo.InvariantCulture) ?? "Unchanged/Inherit";

    /// <summary>The deterministic ordering key for one issue: entity group, then index, then code, then path.</summary>
    private readonly record struct SortKey(int Group, int Index, string Code, string FieldPath);
}
