using System.Text;
using Caisson.Domain.DesiredState;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Caisson.Ingestion.Schema;

/// <summary>Result of <see cref="DesiredStateYamlParser.Parse"/>: either a root node to validate, or one parse error.</summary>
public readonly struct YamlParseResult
{
    private YamlParseResult(YamlMappingNode? root, DesiredStateValidationIssue? error)
    {
        Root = root;
        Error = error;
    }

    /// <summary>The parsed document's root mapping node, when parsing succeeded.</summary>
    public YamlMappingNode? Root { get; }

    /// <summary>The parse error, when parsing failed.</summary>
    public DesiredStateValidationIssue? Error { get; }

    /// <summary>Whether parsing succeeded.</summary>
    public bool IsSuccess => Error is null;

    public static YamlParseResult Ok(YamlMappingNode root) => new(root, null);

    public static YamlParseResult Failed(DesiredStateValidationIssue error) => new(null, error);
}

/// <summary>
/// Loads a rack desired-state YAML file into a node DOM (<see cref="YamlStream"/>/
/// <see cref="YamlMappingNode"/>), never a direct <c>Deserialize&lt;T&gt;</c> (ADR 0025) — the DOM keeps
/// each node's <c>Start</c>/<c>End</c> <see cref="Mark"/>s available so parse errors carry file/line/
/// column (AC2), and lets <see cref="DesiredStateValidator"/> walk the tree explicitly rejecting unknown
/// fields rather than relying on strict-deserialization behaviour. Never throws: a <see cref="YamlException"/>
/// (or any other parse fault) is translated into a <see cref="DesiredStateValidationIssue"/> (NFR8).
/// </summary>
public static class DesiredStateYamlParser
{
    /// <summary>
    /// Parses <paramref name="content"/>. The byte size is checked against
    /// <see cref="DesiredStateSchema.MaxYamlDocumentBytes"/> BEFORE YamlDotNet ever sees the document
    /// (NFR8: no unbounded allocation/parsing time for a hostile oversized or deeply-nested document).
    /// </summary>
    public static YamlParseResult Parse(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount > DesiredStateSchema.MaxYamlDocumentBytes)
        {
            return YamlParseResult.Failed(new DesiredStateValidationIssue(
                filePath,
                "/",
                $"Document is {byteCount} bytes, exceeding the {DesiredStateSchema.MaxYamlDocumentBytes}-byte bound.",
                ValidationSeverity.Error));
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(content));

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return YamlParseResult.Failed(new DesiredStateValidationIssue(
                    filePath, "/", "The document's root must be a YAML mapping.", ValidationSeverity.Error));
            }

            // The desired-state schema is single-document everywhere (git-ingestion and the round-trip both).
            // A stream with '---' separators would silently keep only the first document and drop the rest —
            // a lossy round-trip — so reject it fail-fast rather than dropping content (AC4).
            if (stream.Documents.Count > 1)
            {
                var second = stream.Documents[1].RootNode;
                return YamlParseResult.Failed(new DesiredStateValidationIssue(
                    filePath,
                    "/",
                    $"The document must contain exactly one YAML document; found {stream.Documents.Count}. "
                    + "Remove the '---' document separator(s) and any extra documents.",
                    ValidationSeverity.Error,
                    Line: checked((int)second.Start.Line),
                    Column: checked((int)second.Start.Column)));
            }

            return YamlParseResult.Ok(root);
        }
        catch (YamlException ex)
        {
            return YamlParseResult.Failed(new DesiredStateValidationIssue(
                filePath,
                "/",
                $"YAML parse error: {ex.Message}",
                ValidationSeverity.Error,
                Line: checked((int)ex.Start.Line),
                Column: checked((int)ex.Start.Column)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defense in depth: any other unexpected parser fault still becomes a validation issue, not
            // a crash of the ingestion run (NFR8).
            return YamlParseResult.Failed(new DesiredStateValidationIssue(
                filePath, "/", $"Failed to parse YAML: {ex.Message}", ValidationSeverity.Error));
        }
    }
}
