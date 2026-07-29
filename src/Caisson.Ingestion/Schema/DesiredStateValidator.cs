using Caisson.Domain.DesiredState;
using YamlDotNet.RepresentationModel;

namespace Caisson.Ingestion.Schema;

/// <summary>One rack file's validated, ready-to-materialise field values (story #62, AC3).</summary>
public sealed record ValidatedPort(string Name, int AccessVlan, string? Description, string? NeighborSystemName, string? NeighborPortId);

/// <summary>One rack file's validated switch and its ports.</summary>
public sealed record ValidatedSwitch(string Name, IReadOnlyList<ValidatedPort> Ports);

/// <summary>One rack file's fully validated document, ready for <see cref="Materializer.DesiredStateMaterializer"/>.</summary>
public sealed record ValidatedRackDocument(string RackSlug, IReadOnlyList<ValidatedSwitch> Switches);

/// <summary>The outcome of validating one rack file: either a clean document, or a list of issues.</summary>
public sealed record DesiredStateValidationResult(ValidatedRackDocument? Document, IReadOnlyList<DesiredStateValidationIssue> Issues)
{
    public bool IsValid => Document is not null;
}

/// <summary>
/// Hand-written schema walk over a parsed rack file's YAML node tree (story #62, AC2). Rejects unknown
/// keys explicitly, ACCUMULATES every problem found rather than failing on the first one, and reports
/// JSON-pointer-like locations (e.g. <c>/switches/0/ports/2/accessVlan</c>) alongside line/column where
/// the underlying node has position information. All bounds come from
/// <see cref="DesiredStateSchema"/> — the single audited place they are defined.
/// </summary>
public static class DesiredStateValidator
{
    private const int MaxNodesVisited = 200_000;

    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal) { "rackSlug", "switches" };
    private static readonly HashSet<string> SwitchKeys = new(StringComparer.Ordinal) { "name", "ports" };
    private static readonly HashSet<string> PortKeys = new(StringComparer.Ordinal)
    {
        "name", "accessVlan", "description", "neighbor",
    };
    private static readonly HashSet<string> NeighborKeys = new(StringComparer.Ordinal) { "systemName", "portId" };

    public static DesiredStateValidationResult Validate(string filePath, YamlMappingNode root)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(root);

        var issues = new List<DesiredStateValidationIssue>();
        var nodesVisited = 0;

        RejectUnknownKeys(root, RootKeys, "/", filePath, issues);

        var rackSlug = ReadRequiredString(root, "rackSlug", "/rackSlug", filePath, issues);
        if (rackSlug is not null && !DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            issues.Add(new DesiredStateValidationIssue(
                filePath, "/rackSlug", $"'{rackSlug}' is not a valid rackSlug (expected a DNS-label-shaped value)."));
        }

        if (rackSlug is not null)
        {
            var expectedSlug = Path.GetFileNameWithoutExtension(filePath);
            if (!string.Equals(rackSlug, expectedSlug, StringComparison.Ordinal))
            {
                issues.Add(new DesiredStateValidationIssue(
                    filePath, "/rackSlug",
                    $"rackSlug '{rackSlug}' does not match the file name '{expectedSlug}' (path convention: " +
                    "desired-state/racks/<rackSlug>.yaml)."));
            }
        }

        var switches = new List<ValidatedSwitch>();
        if (!TryGetNode(root, "switches", out var switchesNode))
        {
            issues.Add(new DesiredStateValidationIssue(filePath, "/switches", "'switches' is required."));
        }
        else if (switchesNode is not YamlSequenceNode switchesSeq)
        {
            issues.Add(new DesiredStateValidationIssue(filePath, "/switches", "'switches' must be a list."));
        }
        else if (switchesSeq.Children.Count > DesiredStateSchema.MaxSwitchesPerRack)
        {
            issues.Add(new DesiredStateValidationIssue(
                filePath, "/switches",
                $"'switches' has {switchesSeq.Children.Count} entries, exceeding the " +
                $"{DesiredStateSchema.MaxSwitchesPerRack}-switch bound."));
        }
        else
        {
            var switchNames = new HashSet<string>(StringComparer.Ordinal);
            var totalPorts = 0;

            for (var i = 0; i < switchesSeq.Children.Count; i++)
            {
                if (!GuardNodeBudget(ref nodesVisited, filePath, "/switches", issues))
                {
                    break;
                }

                var switchLocation = $"/switches/{i}";
                if (switchesSeq.Children[i] is not YamlMappingNode switchNode)
                {
                    issues.Add(new DesiredStateValidationIssue(filePath, switchLocation, "Each switch must be a mapping."));
                    continue;
                }

                RejectUnknownKeys(switchNode, SwitchKeys, switchLocation, filePath, issues);

                var switchName = ReadRequiredString(switchNode, "name", $"{switchLocation}/name", filePath, issues);
                if (switchName is not null)
                {
                    if (!DesiredStateSchema.IsValidDeviceName(switchName))
                    {
                        issues.Add(new DesiredStateValidationIssue(
                            filePath, $"{switchLocation}/name", $"'{switchName}' is not a valid switch name."));
                    }
                    else if (!switchNames.Add(switchName))
                    {
                        issues.Add(new DesiredStateValidationIssue(
                            filePath, $"{switchLocation}/name", $"Duplicate switch name '{switchName}' in this rack."));
                    }
                }

                var ports = ValidatePorts(filePath, switchNode, switchLocation, ref nodesVisited, issues);
                totalPorts += ports.Count;
                if (switchName is not null)
                {
                    switches.Add(new ValidatedSwitch(switchName, ports));
                }
            }

            if (totalPorts > DesiredStateSchema.MaxPortsPerRack)
            {
                issues.Add(new DesiredStateValidationIssue(
                    filePath, "/switches",
                    $"This rack defines {totalPorts} ports in total, exceeding the " +
                    $"{DesiredStateSchema.MaxPortsPerRack}-port bound."));
            }
        }

        if (issues.Count > 0 || rackSlug is null)
        {
            return new DesiredStateValidationResult(null, issues);
        }

        return new DesiredStateValidationResult(new ValidatedRackDocument(rackSlug, switches), issues);
    }

    private static List<ValidatedPort> ValidatePorts(
        string filePath, YamlMappingNode switchNode, string switchLocation, ref int nodesVisited,
        List<DesiredStateValidationIssue> issues)
    {
        var ports = new List<ValidatedPort>();

        if (!TryGetNode(switchNode, "ports", out var portsNode))
        {
            issues.Add(new DesiredStateValidationIssue(filePath, $"{switchLocation}/ports", "'ports' is required."));
            return ports;
        }

        if (portsNode is not YamlSequenceNode portsSeq)
        {
            issues.Add(new DesiredStateValidationIssue(filePath, $"{switchLocation}/ports", "'ports' must be a list."));
            return ports;
        }

        var portNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < portsSeq.Children.Count; i++)
        {
            if (!GuardNodeBudget(ref nodesVisited, filePath, $"{switchLocation}/ports", issues))
            {
                break;
            }

            var portLocation = $"{switchLocation}/ports/{i}";
            if (portsSeq.Children[i] is not YamlMappingNode portNode)
            {
                issues.Add(new DesiredStateValidationIssue(filePath, portLocation, "Each port must be a mapping."));
                continue;
            }

            RejectUnknownKeys(portNode, PortKeys, portLocation, filePath, issues);

            var portName = ReadRequiredString(portNode, "name", $"{portLocation}/name", filePath, issues);
            if (portName is not null)
            {
                if (!DesiredStateSchema.IsValidDeviceName(portName))
                {
                    issues.Add(new DesiredStateValidationIssue(
                        filePath, $"{portLocation}/name", $"'{portName}' is not a valid port name."));
                }
                else if (!portNames.Add(portName))
                {
                    issues.Add(new DesiredStateValidationIssue(
                        filePath, $"{portLocation}/name", $"Duplicate port name '{portName}' on this switch."));
                }
            }

            var accessVlan = ReadRequiredVlan(portNode, $"{portLocation}/accessVlan", filePath, issues);

            var description = ReadOptionalString(
                portNode, "description", $"{portLocation}/description", filePath,
                DesiredStateSchema.MaxDescriptionLength, issues);

            string? neighborSystemName = null;
            string? neighborPortId = null;
            if (TryGetNode(portNode, "neighbor", out var neighborNode))
            {
                if (neighborNode is not YamlMappingNode neighborMapping)
                {
                    issues.Add(new DesiredStateValidationIssue(
                        filePath, $"{portLocation}/neighbor", "'neighbor' must be a mapping."));
                }
                else
                {
                    RejectUnknownKeys(neighborMapping, NeighborKeys, $"{portLocation}/neighbor", filePath, issues);
                    neighborSystemName = ReadOptionalString(
                        neighborMapping, "systemName", $"{portLocation}/neighbor/systemName", filePath,
                        DesiredStateSchema.MaxNeighborFieldLength, issues);
                    neighborPortId = ReadOptionalString(
                        neighborMapping, "portId", $"{portLocation}/neighbor/portId", filePath,
                        DesiredStateSchema.MaxNeighborFieldLength, issues);
                }
            }

            if (portName is not null && accessVlan is not null)
            {
                ports.Add(new ValidatedPort(portName, accessVlan.Value, description, neighborSystemName, neighborPortId));
            }
        }

        return ports;
    }

    private static bool GuardNodeBudget(
        ref int nodesVisited, string filePath, string location, List<DesiredStateValidationIssue> issues)
    {
        nodesVisited++;
        if (nodesVisited <= MaxNodesVisited)
        {
            return true;
        }

        issues.Add(new DesiredStateValidationIssue(
            filePath, location, $"Document exceeds the {MaxNodesVisited}-node validation budget."));
        return false;
    }

    private static void RejectUnknownKeys(
        YamlMappingNode node, HashSet<string> allowedKeys, string location, string filePath,
        List<DesiredStateValidationIssue> issues)
    {
        foreach (var key in node.Children.Keys)
        {
            if (key is YamlScalarNode { Value: { } keyName } && !allowedKeys.Contains(keyName))
            {
                issues.Add(new DesiredStateValidationIssue(
                    filePath, $"{location.TrimEnd('/')}/{keyName}", $"Unknown field '{keyName}'."));
            }
        }
    }

    private static bool TryGetNode(YamlMappingNode node, string key, out YamlNode value)
    {
        foreach (var (childKey, childValue) in node.Children)
        {
            if (childKey is YamlScalarNode { Value: { } keyName } && string.Equals(keyName, key, StringComparison.Ordinal))
            {
                value = childValue;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static string? ReadRequiredString(
        YamlMappingNode node, string key, string location, string filePath, List<DesiredStateValidationIssue> issues)
    {
        if (!TryGetNode(node, key, out var value))
        {
            issues.Add(new DesiredStateValidationIssue(filePath, location, $"'{key}' is required."));
            return null;
        }

        if (value is not YamlScalarNode { Value: { Length: > 0 } text })
        {
            issues.Add(new DesiredStateValidationIssue(filePath, location, $"'{key}' must be a non-empty string."));
            return null;
        }

        return text;
    }

    private static string? ReadOptionalString(
        YamlMappingNode node, string key, string location, string filePath, int maxLength,
        List<DesiredStateValidationIssue> issues)
    {
        if (!TryGetNode(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlScalarNode scalar)
        {
            issues.Add(new DesiredStateValidationIssue(filePath, location, $"'{key}' must be a string."));
            return null;
        }

        var text = scalar.Value ?? string.Empty;
        if (text.Length > maxLength)
        {
            issues.Add(new DesiredStateValidationIssue(
                filePath, location, $"'{key}' exceeds the {maxLength}-character bound."));
            return null;
        }

        return text;
    }

    private static int? ReadRequiredVlan(
        YamlMappingNode node, string location, string filePath, List<DesiredStateValidationIssue> issues)
    {
        if (!TryGetNode(node, "accessVlan", out var value))
        {
            issues.Add(new DesiredStateValidationIssue(filePath, location, "'accessVlan' is required."));
            return null;
        }

        if (value is not YamlScalarNode { Value: { } text }
            || !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var vlan))
        {
            issues.Add(new DesiredStateValidationIssue(
                filePath, location, "'accessVlan' must be an integer."));
            return null;
        }

        if (vlan < DesiredStateSchema.MinVlan || vlan > DesiredStateSchema.MaxVlan)
        {
            issues.Add(new DesiredStateValidationIssue(
                filePath, location,
                $"'accessVlan' value {vlan} is out of range " +
                $"[{DesiredStateSchema.MinVlan}, {DesiredStateSchema.MaxVlan}]."));
            return null;
        }

        return vlan;
    }
}
