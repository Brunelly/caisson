using System.Globalization;
using System.Text.RegularExpressions;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.Schema;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;
using YamlDotNet.RepresentationModel;

namespace Caisson.Ingestion.RoundTrip;

/// <summary>One import problem: a dotted document path (e.g. <c>spec.vlans[2].vlanId</c>) plus a message.</summary>
public sealed record DesiredStateImportIssue(string Path, string Message, int? Line = null, int? Column = null);

/// <summary>The outcome of an import: either the full envelope, or an accumulated issue list and NO model (AC4).</summary>
public sealed record DesiredStateImportResult(
    DesiredStateRoundTripEnvelope? Envelope, IReadOnlyList<DesiredStateImportIssue> Issues)
{
    /// <summary>Whether the import produced a model.</summary>
    public bool IsSuccess => Envelope is not null;
}

/// <summary>
/// Safe round-trip YAML importer (story #169, Task #183). Reuses <see cref="DesiredStateYamlParser.Parse"/>
/// for the bounded, never-throwing DOM load (byte-size guard BEFORE parsing, line/column on syntax error),
/// then walks the DOM in the style of <see cref="DesiredStateValidator"/> — accumulating every issue with a
/// dotted path and a node-count budget — to:
/// <list type="bullet">
/// <item>extract the UI-supported model (VLAN catalogue + per-port access-VLAN intents);</item>
/// <item>reject any unknown top-level key EXCEPT <c>extensions</c>, and any unknown key inside
/// <c>spec</c>/<c>metadata</c>/a vlan/a switch/a port (fail-fast, Q2 answer);</item>
/// <item>capture the reserved <c>extensions</c> block byte-for-byte by slicing the ORIGINAL source between
/// DOM node marks — never re-serializing the node — into a <see cref="PreservedYamlBlock"/> with a checksum;</item>
/// <item>detect YAML comments via a <see cref="Scanner"/> token pass (deterministic, robust against <c>#</c>
/// inside quoted scalars) and raise <see cref="DesiredStateRoundTripWarningCode.CommentsNotPreserved"/> for any
/// comment outside the opaque <c>extensions</c> bytes.</item>
/// </list>
/// On ANY error the importer returns an accumulated issue list and NO partial model. For v1 the UI
/// <see cref="PortAccessIntent.SwitchStableKey"/> is the YAML switch <c>name</c> directly (ADR 0049/0050).
/// </summary>
public static class DesiredStateYamlImporter
{
    private const string DocumentLabel = "desired-state.yaml";
    private const int MaxNodesVisited = 200_000;

    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "spec", DesiredStateYamlSchema.ExtensionsKey,
    };

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal) { "rackSlug" };
    private static readonly HashSet<string> SpecKeys = new(StringComparer.Ordinal) { "vlans", "switches" };
    private static readonly HashSet<string> VlanKeys = new(StringComparer.Ordinal) { "vlanId", "name", "description" };
    private static readonly HashSet<string> SwitchKeys = new(StringComparer.Ordinal) { "name", "ports" };

    // v1 supported port keys: name + accessVlan only. `description`/`neighbor` are reserved in the schema
    // ordering for a future convergence story but rejected here so the round-trip stays lossless (ADR 0050).
    private static readonly HashSet<string> PortKeys = new(StringComparer.Ordinal) { "name", "accessVlan" };

    private static readonly Regex ValidatorFieldPattern = new(
        @"^(vlanCatalogue|portIntents)\[(\d+)\]\.(\w+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Imports <paramref name="yaml"/> into a round-trip envelope, or an accumulated issue list.</summary>
    public static DesiredStateImportResult Import(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var parsed = DesiredStateYamlParser.Parse(DocumentLabel, yaml);
        if (!parsed.IsSuccess)
        {
            var e = parsed.Error!;
            return Failure(new DesiredStateImportIssue(NormalizeRootPath(e.Location), e.Message, e.Line, e.Column));
        }

        var root = parsed.Root!;
        var issues = new List<DesiredStateImportIssue>();
        var budget = new NodeBudget();

        RejectUnknownKeys(root, RootKeys, string.Empty, issues);

        var rackSlug = ReadApiHeaderAndSlug(root, issues);

        var vlanCatalogue = new List<VlanCatalogueEntry>();
        var vlanPaths = new List<string>();
        var portIntents = new List<PortAccessIntent>();
        var portPaths = new List<string>();
        var portSwitchPaths = new List<string>();

        if (TryGetChild(root, "spec", out var specNode))
        {
            if (specNode is not YamlMappingNode specMapping)
            {
                issues.Add(new DesiredStateImportIssue("spec", "'spec' must be a mapping."));
            }
            else
            {
                RejectUnknownKeys(specMapping, SpecKeys, "spec", issues);
                ReadVlans(specMapping, vlanCatalogue, vlanPaths, budget, issues);
                ReadSwitches(specMapping, portIntents, portPaths, portSwitchPaths, budget, issues);
            }
        }
        else
        {
            issues.Add(new DesiredStateImportIssue("spec", "'spec' is required."));
        }

        // Any structural/schema issue: fail-fast with the accumulated list, no partial model (AC4).
        if (issues.Count > 0)
        {
            return new DesiredStateImportResult(null, issues);
        }

        // Structurally clean: the built lists are aligned with their YAML paths and well-formed, so semantic
        // rules (range/duplicate/cross-reference/length) come from the ONE shared validator, mapped back to
        // YAML paths (e.g. spec.vlans[2].vlanId).
        var semantic = NetworkIntentValidator.Validate(vlanCatalogue, portIntents);
        if (semantic.Count > 0)
        {
            foreach (var (field, message) in semantic)
            {
                issues.Add(new DesiredStateImportIssue(
                    TranslateValidatorField(field, vlanPaths, portPaths, portSwitchPaths), message));
            }

            return new DesiredStateImportResult(null, issues);
        }

        var extensions = CaptureExtensions(root, yaml);
        var warnings = DetectComments(yaml, extensions);

        var envelope = new DesiredStateRoundTripEnvelope(
            new SupportedDesiredStateModel(rackSlug!, vlanCatalogue, portIntents),
            extensions is null ? Array.Empty<PreservedYamlBlock>() : new[] { extensions },
            warnings,
            DesiredStateSchema.CurrentSchemaVersion);

        return new DesiredStateImportResult(envelope, Array.Empty<DesiredStateImportIssue>());
    }

    private static string? ReadApiHeaderAndSlug(YamlMappingNode root, List<DesiredStateImportIssue> issues)
    {
        var apiVersion = ReadRequiredString(root, "apiVersion", "apiVersion", issues);
        if (apiVersion is not null && !string.Equals(apiVersion, DesiredStateYamlSchema.ApiVersion, StringComparison.Ordinal))
        {
            issues.Add(new DesiredStateImportIssue(
                "apiVersion", $"Unsupported apiVersion '{apiVersion}'; expected '{DesiredStateYamlSchema.ApiVersion}'."));
        }

        var kind = ReadRequiredString(root, "kind", "kind", issues);
        if (kind is not null && !string.Equals(kind, DesiredStateYamlSchema.Kind, StringComparison.Ordinal))
        {
            issues.Add(new DesiredStateImportIssue(
                "kind", $"Unsupported kind '{kind}'; expected '{DesiredStateYamlSchema.Kind}'."));
        }

        if (!TryGetChild(root, "metadata", out var metadataNode))
        {
            issues.Add(new DesiredStateImportIssue("metadata", "'metadata' is required."));
            return null;
        }

        if (metadataNode is not YamlMappingNode metadataMapping)
        {
            issues.Add(new DesiredStateImportIssue("metadata", "'metadata' must be a mapping."));
            return null;
        }

        RejectUnknownKeys(metadataMapping, MetadataKeys, "metadata", issues);
        var rackSlug = ReadRequiredString(metadataMapping, "rackSlug", "metadata.rackSlug", issues);
        if (rackSlug is not null && !DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            issues.Add(new DesiredStateImportIssue(
                "metadata.rackSlug", $"'{rackSlug}' is not a valid rackSlug (expected a DNS-label-shaped value)."));
        }

        return rackSlug;
    }

    private static void ReadVlans(
        YamlMappingNode specMapping, List<VlanCatalogueEntry> vlanCatalogue, List<string> vlanPaths,
        NodeBudget budget, List<DesiredStateImportIssue> issues)
    {
        if (!TryGetChild(specMapping, "vlans", out var vlansNode))
        {
            return; // vlans is optional; absent => empty catalogue (renderer emits `vlans: []`).
        }

        if (vlansNode is not YamlSequenceNode vlansSeq)
        {
            issues.Add(new DesiredStateImportIssue("spec.vlans", "'vlans' must be a list."));
            return;
        }

        for (var i = 0; i < vlansSeq.Children.Count; i++)
        {
            if (!budget.Consume(issues, "spec.vlans"))
            {
                return;
            }

            var path = $"spec.vlans[{i}]";
            if (vlansSeq.Children[i] is not YamlMappingNode vlanNode)
            {
                issues.Add(new DesiredStateImportIssue(path, "Each VLAN must be a mapping."));
                continue;
            }

            RejectUnknownKeys(vlanNode, VlanKeys, path, issues);
            var vlanId = ReadRequiredInt(vlanNode, "vlanId", $"{path}.vlanId", issues);
            var name = ReadRequiredString(vlanNode, "name", $"{path}.name", issues);
            var description = ReadOptionalString(vlanNode, "description", $"{path}.description", issues);

            if (vlanId is not null && name is not null)
            {
                vlanCatalogue.Add(new VlanCatalogueEntry(vlanId.Value, name, description));
                vlanPaths.Add(path);
            }
        }
    }

    private static void ReadSwitches(
        YamlMappingNode specMapping, List<PortAccessIntent> portIntents, List<string> portPaths,
        List<string> portSwitchPaths, NodeBudget budget, List<DesiredStateImportIssue> issues)
    {
        if (!TryGetChild(specMapping, "switches", out var switchesNode))
        {
            return; // switches is optional; absent => no port intents (renderer emits `switches: []`).
        }

        if (switchesNode is not YamlSequenceNode switchesSeq)
        {
            issues.Add(new DesiredStateImportIssue("spec.switches", "'switches' must be a list."));
            return;
        }

        for (var s = 0; s < switchesSeq.Children.Count; s++)
        {
            if (!budget.Consume(issues, "spec.switches"))
            {
                return;
            }

            var switchPath = $"spec.switches[{s}]";
            if (switchesSeq.Children[s] is not YamlMappingNode switchNode)
            {
                issues.Add(new DesiredStateImportIssue(switchPath, "Each switch must be a mapping."));
                continue;
            }

            RejectUnknownKeys(switchNode, SwitchKeys, switchPath, issues);
            var switchName = ReadRequiredString(switchNode, "name", $"{switchPath}.name", issues);

            if (!TryGetChild(switchNode, "ports", out var portsNode))
            {
                issues.Add(new DesiredStateImportIssue($"{switchPath}.ports", "'ports' is required."));
                continue;
            }

            if (portsNode is not YamlSequenceNode portsSeq)
            {
                issues.Add(new DesiredStateImportIssue($"{switchPath}.ports", "'ports' must be a list."));
                continue;
            }

            for (var p = 0; p < portsSeq.Children.Count; p++)
            {
                if (!budget.Consume(issues, $"{switchPath}.ports"))
                {
                    return;
                }

                var portPath = $"{switchPath}.ports[{p}]";
                if (portsSeq.Children[p] is not YamlMappingNode portNode)
                {
                    issues.Add(new DesiredStateImportIssue(portPath, "Each port must be a mapping."));
                    continue;
                }

                RejectUnknownKeys(portNode, PortKeys, portPath, issues);
                var portName = ReadRequiredString(portNode, "name", $"{portPath}.name", issues);
                var accessVlan = ReadRequiredInt(portNode, "accessVlan", $"{portPath}.accessVlan", issues);

                if (switchName is not null && portName is not null && accessVlan is not null)
                {
                    portIntents.Add(new PortAccessIntent(switchName, portName, accessVlan.Value));
                    portPaths.Add(portPath);
                    portSwitchPaths.Add(switchPath);
                }
            }
        }
    }

    /// <summary>
    /// Captures the reserved <c>extensions</c> top-level block byte-for-byte by slicing the ORIGINAL source
    /// between the <c>extensions</c> key's start mark and the next top-level key's start mark (or EOF), so
    /// original indentation and line endings are preserved exactly. Returns <c>null</c> when no extensions
    /// block is present.
    /// </summary>
    private static PreservedYamlBlock? CaptureExtensions(YamlMappingNode root, string content)
    {
        long? extStart = null;
        var boundaries = new List<long>();
        foreach (var (keyNode, _) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } keyName })
            {
                continue;
            }

            boundaries.Add(keyNode.Start.Index);
            if (string.Equals(keyName, DesiredStateYamlSchema.ExtensionsKey, StringComparison.Ordinal))
            {
                extStart = keyNode.Start.Index;
            }
        }

        if (extStart is null)
        {
            return null;
        }

        // The next top-level key that begins after `extensions` in the source bounds the block; else EOF.
        var end = content.Length;
        foreach (var boundary in boundaries)
        {
            if (boundary > extStart.Value && boundary < end)
            {
                end = (int)boundary;
            }
        }

        var start = (int)extStart.Value;
        if (start < 0 || start > content.Length || end < start)
        {
            // Defensive fallback: mark edge case — anchor from the extensions key line to EOF.
            start = Math.Clamp(start, 0, content.Length);
            end = content.Length;
        }

        var raw = content.Substring(start, end - start);
        return PreservedYamlBlock.Create(DesiredStateYamlSchema.ExtensionsKey, raw);
    }

    /// <summary>
    /// Detects YAML comments via a <see cref="Scanner"/> token pass (robust against <c>#</c> inside quoted
    /// scalars). A comment strictly inside the opaque <paramref name="extensions"/> byte range is preserved
    /// with that block and does NOT warn; any comment outside it raises
    /// <see cref="DesiredStateRoundTripWarningCode.CommentsNotPreserved"/> (AC3).
    /// </summary>
    private static IReadOnlyList<DesiredStateRoundTripWarningCode> DetectComments(
        string content, PreservedYamlBlock? extensions)
    {
        long extStart = -1;
        long extEnd = -1;
        if (extensions is not null)
        {
            extStart = content.IndexOf(extensions.RawYamlText, StringComparison.Ordinal);
            if (extStart >= 0)
            {
                extEnd = extStart + extensions.RawYamlText.Length;
            }
        }

        try
        {
            var scanner = new Scanner(new StringReader(content), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is not Comment comment)
                {
                    continue;
                }

                var position = comment.Start.Index;
                var insideExtensions = extStart >= 0 && position >= extStart && position < extEnd;
                if (!insideExtensions)
                {
                    return new[] { DesiredStateRoundTripWarningCode.CommentsNotPreserved };
                }
            }
        }
        catch (YamlException)
        {
            // The DOM already parsed successfully, so this should not happen; a scanner fault is not a reason
            // to fail an otherwise-valid import — simply report no comment warning.
        }

        return Array.Empty<DesiredStateRoundTripWarningCode>();
    }

    private static string TranslateValidatorField(
        string field, IReadOnlyList<string> vlanPaths, IReadOnlyList<string> portPaths, IReadOnlyList<string> portSwitchPaths)
    {
        var match = ValidatorFieldPattern.Match(field);
        if (!match.Success)
        {
            return field;
        }

        var collection = match.Groups[1].Value;
        var index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var suffix = match.Groups[3].Value;

        if (collection == "vlanCatalogue" && index < vlanPaths.Count)
        {
            var subField = suffix switch
            {
                "id" => "vlanId",
                _ => suffix,
            };
            return $"{vlanPaths[index]}.{subField}";
        }

        if (collection == "portIntents" && index < portPaths.Count)
        {
            return suffix switch
            {
                "switchStableKey" => $"{portSwitchPaths[index]}.name",
                "portName" => $"{portPaths[index]}.name",
                "accessVlanId" => $"{portPaths[index]}.accessVlan",
                _ => $"{portPaths[index]}.{suffix}",
            };
        }

        return field;
    }

    private static void RejectUnknownKeys(
        YamlMappingNode node, HashSet<string> allowedKeys, string pathPrefix, List<DesiredStateImportIssue> issues)
    {
        foreach (var key in node.Children.Keys)
        {
            if (key is YamlScalarNode { Value: { } keyName } && !allowedKeys.Contains(keyName))
            {
                issues.Add(new DesiredStateImportIssue(JoinPath(pathPrefix, keyName), $"Unknown field '{keyName}'."));
            }
        }
    }

    private static bool TryGetChild(YamlMappingNode node, string key, out YamlNode value)
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
        YamlMappingNode node, string key, string path, List<DesiredStateImportIssue> issues)
    {
        if (!TryGetChild(node, key, out var value))
        {
            issues.Add(new DesiredStateImportIssue(path, $"'{key}' is required."));
            return null;
        }

        if (value is not YamlScalarNode { Value: { Length: > 0 } text })
        {
            issues.Add(new DesiredStateImportIssue(path, $"'{key}' must be a non-empty string."));
            return null;
        }

        return text;
    }

    private static string? ReadOptionalString(
        YamlMappingNode node, string key, string path, List<DesiredStateImportIssue> issues)
    {
        if (!TryGetChild(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlScalarNode scalar)
        {
            issues.Add(new DesiredStateImportIssue(path, $"'{key}' must be a string."));
            return null;
        }

        return scalar.Value ?? string.Empty;
    }

    private static int? ReadRequiredInt(
        YamlMappingNode node, string key, string path, List<DesiredStateImportIssue> issues)
    {
        if (!TryGetChild(node, key, out var value))
        {
            issues.Add(new DesiredStateImportIssue(path, $"'{key}' is required."));
            return null;
        }

        if (value is not YamlScalarNode { Value: { } text }
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            issues.Add(new DesiredStateImportIssue(path, $"'{key}' must be an integer."));
            return null;
        }

        return parsed;
    }

    private static string JoinPath(string prefix, string key)
        => prefix.Length == 0 ? key : $"{prefix}.{key}";

    private static string NormalizeRootPath(string location)
        => location is "/" or "" ? "$" : location;

    private static DesiredStateImportResult Failure(DesiredStateImportIssue issue)
        => new(null, new[] { issue });

    /// <summary>Bounds total nodes visited to defeat billion-laughs alias expansion (NFR3).</summary>
    private sealed class NodeBudget
    {
        private int _visited;
        private bool _reported;

        public bool Consume(List<DesiredStateImportIssue> issues, string path)
        {
            _visited++;
            if (_visited <= MaxNodesVisited)
            {
                return true;
            }

            if (!_reported)
            {
                issues.Add(new DesiredStateImportIssue(path, $"Document exceeds the {MaxNodesVisited}-node budget."));
                _reported = true;
            }

            return false;
        }
    }
}
