using Caisson.Domain.Security;
using Caisson.Domain.Topology;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// One actionable validation error surfaced for a rack file that failed schema validation or YAML
/// parsing (story #62, AC2). Append-only: rows are inserted once per ingestion run and never updated
/// (NFR7) — a rack file is either accepted (a new <see cref="DesiredStateVersion"/>) or rejected (only
/// these error rows), never both, and a later run's errors are new rows, not edits to old ones.
/// </summary>
public sealed class DesiredStateValidationError : IAppendOnly
{
    /// <summary>Maximum length of <see cref="FilePath"/>.</summary>
    public const int MaxFilePathLength = DesiredStateSchema.MaxFilePathLength;

    /// <summary>Maximum length of <see cref="Location"/>.</summary>
    public const int MaxLocationLength = DesiredStateSchema.MaxLocationLength;

    /// <summary>Maximum length of <see cref="Message"/>.</summary>
    public const int MaxMessageLength = DesiredStateSchema.MaxValidationMessageLength;

    private DesiredStateValidationError()
    {
        // EF Core materialization constructor.
        RackSlug = null!;
        FilePath = null!;
        Location = null!;
        Message = null!;
    }

    public DesiredStateValidationError(
        Guid id,
        Guid ingestionRunId,
        string rackSlug,
        string filePath,
        string location,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error,
        int? line = null,
        int? column = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rackSlug);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(location);
        ArgumentException.ThrowIfNullOrEmpty(message);

        if (filePath.Length > MaxFilePathLength)
        {
            throw new ArgumentException($"filePath exceeds the {MaxFilePathLength}-character bound.", nameof(filePath));
        }

        if (location.Length > MaxLocationLength)
        {
            throw new ArgumentException($"location exceeds the {MaxLocationLength}-character bound.", nameof(location));
        }

        Id = id;
        IngestionRunId = ingestionRunId;
        RackSlug = rackSlug;
        FilePath = filePath;
        Location = location;
        // A YAML parse-error message can echo raw document content; scrub before bounding, matching the
        // TopologyAuditEvent.DetailsJson precedent (finding #27's value-level backstop).
        Message = Truncate(message);
        Severity = severity;
        Line = line;
        Column = column;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The ingestion run this error was raised during.</summary>
    public Guid IngestionRunId { get; private set; }

    /// <summary>The rack slug the offending file claimed (or was expected to claim from its path).</summary>
    public string RackSlug { get; private set; }

    /// <summary>Repository-relative path of the offending file.</summary>
    public string FilePath { get; private set; }

    /// <summary>JSON-pointer-like location within the file (e.g. <c>/switches/0/ports/2/accessVlan</c>).</summary>
    public string Location { get; private set; }

    /// <summary>Human-readable, operator-safe message.</summary>
    public string Message { get; private set; }

    /// <summary>Severity of the error.</summary>
    public ValidationSeverity Severity { get; private set; }

    /// <summary>1-based line number, for YAML syntax errors.</summary>
    public int? Line { get; private set; }

    /// <summary>1-based column number, for YAML syntax errors.</summary>
    public int? Column { get; private set; }

    private static string Truncate(string message)
    {
        var scrubbed = SecretScrubber.Scrub(message) ?? string.Empty;
        return scrubbed.Length > MaxMessageLength ? scrubbed[..MaxMessageLength] : scrubbed;
    }
}
