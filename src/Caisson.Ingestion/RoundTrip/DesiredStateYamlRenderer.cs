using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;

namespace Caisson.Ingestion.RoundTrip;

/// <summary>Thrown when <see cref="DesiredStateYamlRenderer.Render"/> is handed a semantically invalid model.</summary>
public sealed class DesiredStateRenderException : InvalidOperationException
{
    public DesiredStateRenderException(IReadOnlyList<(string Field, string Message)> errors)
        : base("The supported model is not valid and cannot be rendered.")
        => Errors = errors;

    /// <summary>The field-scoped validation errors that blocked rendering.</summary>
    public IReadOnlyList<(string Field, string Message)> Errors { get; }
}

/// <summary>The result of a render: the canonical YAML document and any non-fatal warnings carried through.</summary>
public sealed record DesiredStateRenderResult(string Yaml, IReadOnlyList<DesiredStateRoundTripWarningCode> Warnings);

/// <summary>
/// Deterministic, hand-written YAML emitter for the round-trip supported subset (story #169, Task #184).
/// Deliberately a <see cref="StringBuilder"/> emitter, NOT YamlDotNet's <c>ISerializer</c> — the project
/// controls the bytes it writes (ADR 0025, cf. <c>DesiredStatePayloadSerializer</c>). Guarantees:
/// <list type="bullet">
/// <item>Repeated renders of the same model are byte-identical UTF-8, LF-only (AC1/NFR1).</item>
/// <item>Lists are emitted in <see cref="DesiredStateYamlSchema"/> sort-key order, never insertion order.</item>
/// <item>Output is locale-independent (numbers via <see cref="CultureInfo.InvariantCulture"/>, names via Ordinal).</item>
/// <item><c>metadata.rackSlug</c> is validated with <see cref="DesiredStateSchema.IsValidRackSlug"/> — the SAME
/// predicate the importer enforces — so the renderer never emits a document its own parser would reject
/// (AC1 schema-conformance, AC2 export→re-import; see the note on <see cref="Render"/>).</item>
/// <item>Preserved <c>extensions</c> blocks are re-emitted byte-for-byte at the canonical last position after
/// verifying each block is anchored at the reserved <c>extensions</c> key and its checksum matches; either
/// check failing is rejected, never silently written (AC2).</item>
/// </list>
/// </summary>
public static class DesiredStateYamlRenderer
{
    private const string Newline = DesiredStateYamlSchema.Newline;

    // Chars that make a plain scalar ambiguous anywhere in the token; any hit forces double-quoting. This is
    // the single documented quoting predicate (AC1 example: booleans/null-like/numeric-looking/colon-hash).
    private static readonly char[] SignificantChars =
    {
        ':', '#', ',', '[', ']', '{', '}', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`',
    };

    // A leading indicator char makes even an otherwise-plain token ambiguous.
    private static readonly char[] LeadingIndicators =
    {
        '-', '?', ':', ',', '[', ']', '{', '}', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', ' ', '\t',
    };

    private static readonly Regex NumericLike = new(
        @"^[+-]?(\d[\d_]*\.?[\d_]*|\.[\d_]+)([eE][+-]?\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OctalOrHexLike = new(
        @"^0(x[0-9a-fA-F]+|o?[0-7]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NonStringScalars = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "~", "true", "false", "yes", "no", "on", "off", "y", "n",
    };

    /// <summary>
    /// Renders <paramref name="model"/> plus its preserved <paramref name="unknownBlocks"/> to canonical YAML.
    /// Validates <c>metadata.rackSlug</c> and re-runs <see cref="NetworkIntentValidator"/>, throwing
    /// <see cref="DesiredStateRenderException"/> rather than ever emitting an invalid document. Caller
    /// collections are never mutated.
    /// <para>
    /// The <c>rackSlug</c> is server-authoritative: the caller resolves it from the target rack (its
    /// <c>ExternalKey</c>), NOT from any imported <c>metadata.rackSlug</c> (which the round trip discards).
    /// Because <c>ExternalKey</c> is only length-bounded, a rack whose key is not DNS-label-shaped would
    /// otherwise render a document the parser rejects; validating here closes that AC1/AC2 gap symmetrically
    /// with the importer (which enforces the same <see cref="DesiredStateSchema.IsValidRackSlug"/>).
    /// </para>
    /// </summary>
    public static DesiredStateRenderResult Render(
        SupportedDesiredStateModel model,
        IReadOnlyList<PreservedYamlBlock>? unknownBlocks = null,
        IReadOnlyList<DesiredStateRoundTripWarningCode>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(model.RackSlug);

        if (!DesiredStateSchema.IsValidRackSlug(model.RackSlug))
        {
            throw new DesiredStateRenderException(new[]
            {
                ("metadata.rackSlug",
                    $"'{model.RackSlug}' is not a valid rackSlug (expected a DNS-label-shaped value of at most "
                    + $"{DesiredStateSchema.MaxRackSlugLength} characters); the rack's external key cannot be exported as a slug."),
            });
        }

        var vlanCatalogue = model.VlanCatalogue ?? Array.Empty<VlanCatalogueEntry>();
        var portIntents = model.PortIntents ?? Array.Empty<PortAccessIntent>();

        var errors = NetworkIntentValidator.Validate(vlanCatalogue, portIntents);
        if (errors.Count > 0)
        {
            throw new DesiredStateRenderException(errors);
        }

        var sb = new StringBuilder();

        // Header + metadata (top-level key order: apiVersion, kind, metadata, spec, extensions).
        AppendScalar(sb, 0, "apiVersion", DesiredStateYamlSchema.ApiVersion);
        AppendScalar(sb, 0, "kind", DesiredStateYamlSchema.Kind);
        AppendKey(sb, 0, "metadata");
        AppendScalar(sb, 1, "rackSlug", model.RackSlug);

        // spec (key order: vlans, switches).
        AppendKey(sb, 0, "spec");
        AppendVlans(sb, vlanCatalogue);
        AppendSwitches(sb, portIntents);

        var generated = sb.ToString();

        // Preserved extensions block(s): re-emitted VERBATIM at the canonical last position, after verifying
        // each block's checksum. The generated section always ends in exactly one LF; the block bytes are
        // opaque (original indentation/line-endings untouched), so this is the one place non-LF bytes may
        // appear in the output — that is the byte-for-byte preservation guarantee (AC2).
        if (unknownBlocks is { Count: > 0 })
        {
            var appended = new StringBuilder(generated);
            foreach (var block in unknownBlocks)
            {
                // v1 preserves exactly one anchor: the reserved top-level `extensions` key. Refuse any block
                // claiming a different anchor so a re-emitted block can never smuggle in a forged top-level key.
                if (!string.Equals(block.AnchorPath, DesiredStateYamlSchema.ExtensionsKey, StringComparison.Ordinal))
                {
                    throw new DesiredStateRenderException(new[]
                    {
                        ($"extensions:{block.AnchorPath}",
                            $"Preserved block anchor '{block.AnchorPath}' is not the reserved "
                            + $"'{DesiredStateYamlSchema.ExtensionsKey}' block; refusing to emit it."),
                    });
                }

                // The checksum only detects corruption of RawYamlText in transit/storage between capture and
                // render — it is NOT an authenticity control against the caller who supplies the bytes.
                if (!block.ChecksumMatches())
                {
                    throw new DesiredStateRenderException(new[]
                    {
                        ($"extensions:{block.AnchorPath}",
                            "Preserved block checksum does not match its content; refusing to emit a corrupted block."),
                    });
                }

                appended.Append(block.RawYamlText);
            }

            return new DesiredStateRenderResult(appended.ToString(), warnings ?? Array.Empty<DesiredStateRoundTripWarningCode>());
        }

        return new DesiredStateRenderResult(generated, warnings ?? Array.Empty<DesiredStateRoundTripWarningCode>());
    }

    private static void AppendVlans(StringBuilder sb, IReadOnlyList<VlanCatalogueEntry> vlanCatalogue)
    {
        if (vlanCatalogue.Count == 0)
        {
            AppendEmptySequence(sb, 1, "vlans");
            return;
        }

        AppendKey(sb, 1, "vlans");
        var sorted = vlanCatalogue.ToList();
        sorted.Sort(DesiredStateYamlSchema.VlanCatalogueOrder);
        foreach (var vlan in sorted)
        {
            AppendSequenceItemScalar(sb, 2, "vlanId", FormatInt(vlan.Id));
            AppendScalar(sb, 3, "name", vlan.Name);
            if (!string.IsNullOrEmpty(vlan.Description))
            {
                AppendScalar(sb, 3, "description", vlan.Description!);
            }
        }
    }

    private static void AppendSwitches(StringBuilder sb, IReadOnlyList<PortAccessIntent> portIntents)
    {
        // Group non-null-intent ports by switch (v1: switchStableKey IS the YAML switch name — ADR 0049).
        // A null AccessVlanId is "no intent" and is omitted entirely ("no row = no intent").
        var switches = portIntents
            .Where(p => p.AccessVlanId is not null)
            .GroupBy(p => p.SwitchStableKey, DesiredStateYamlSchema.NameOrdinal)
            .Select(g => (Name: g.Key, Ports: g
                .OrderBy(p => p.PortName, DesiredStateYamlSchema.NameOrdinal)
                .ToList()))
            .OrderBy(s => s.Name, DesiredStateYamlSchema.NameOrdinal)
            .ToList();

        if (switches.Count == 0)
        {
            AppendEmptySequence(sb, 1, "switches");
            return;
        }

        AppendKey(sb, 1, "switches");
        foreach (var (name, ports) in switches)
        {
            AppendSequenceItemString(sb, 2, "name", name);
            AppendKey(sb, 3, "ports");
            foreach (var port in ports)
            {
                AppendSequenceItemString(sb, 4, "name", port.PortName);
                AppendRawScalar(sb, 5, "accessVlan", FormatInt(port.AccessVlanId!.Value));
            }
        }
    }

    private static void AppendKey(StringBuilder sb, int level, string key)
        => sb.Append(Indent(level)).Append(key).Append(':').Append(Newline);

    private static void AppendEmptySequence(StringBuilder sb, int level, string key)
        => sb.Append(Indent(level)).Append(key).Append(": []").Append(Newline);

    /// <summary>Emits <c>key: value</c> where the value is a plain string that will be quoted as needed.</summary>
    private static void AppendScalar(StringBuilder sb, int level, string key, string value)
        => sb.Append(Indent(level)).Append(key).Append(": ").Append(Quote(value)).Append(Newline);

    /// <summary>Emits <c>key: value</c> where the value is a pre-formatted, unambiguous scalar (e.g. an integer).</summary>
    private static void AppendRawScalar(StringBuilder sb, int level, string key, string formattedValue)
        => sb.Append(Indent(level)).Append(key).Append(": ").Append(formattedValue).Append(Newline);

    /// <summary>Emits the FIRST key of a sequence item (with the <c>- </c> dash); value is pre-formatted.</summary>
    private static void AppendSequenceItemScalar(StringBuilder sb, int level, string key, string formattedValue)
        => sb.Append(Indent(level)).Append("- ").Append(key).Append(": ").Append(formattedValue).Append(Newline);

    /// <summary>
    /// Emits the FIRST key of a sequence item whose value is a raw <b>string</b> — quotes it internally with the
    /// same <see cref="Quote"/> predicate as <see cref="AppendScalar"/>, so a caller can never accidentally emit
    /// an unquoted, potentially-ambiguous sequence-item string (the quoting rule stays uniform for string fields).
    /// </summary>
    private static void AppendSequenceItemString(StringBuilder sb, int level, string key, string value)
        => AppendSequenceItemScalar(sb, level, key, Quote(value));

    private static string Indent(int level) => new(' ', level * DesiredStateYamlSchema.IndentSize);

    private static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The single documented scalar-quoting predicate: quote when the token is empty, has leading/trailing
    /// whitespace, contains a YAML-significant char, starts with an indicator, or would parse as a non-string
    /// scalar (<c>true</c>/<c>null</c>/<c>0100</c>/a number). Quoted output is double-quoted with escapes, so
    /// it is always a single line and locale-independent.
    /// </summary>
    internal static string Quote(string value)
    {
        return NeedsQuoting(value) ? DoubleQuote(value) : value;
    }

    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        if (LeadingIndicators.Contains(value[0]))
        {
            return true;
        }

        if (NonStringScalars.Contains(value) || NumericLike.IsMatch(value) || OctalOrHexLike.IsMatch(value))
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || Array.IndexOf(SignificantChars, ch) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string DoubleQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
