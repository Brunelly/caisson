using System.Text.RegularExpressions;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// Single audited place for every bound the constrained M1 desired-state YAML schema and its
/// ingestion pipeline enforce (story #62, AC2/NFR8). Centralising these constants means the
/// constructor guards on the entities below, the hand-written schema validator in
/// <c>Caisson.Ingestion</c>, and the EF Core <c>HasMaxLength</c>/<c>CHECK</c> mappings in
/// <c>Caisson.Infrastructure</c> can never drift from one another.
/// </summary>
public static partial class DesiredStateSchema
{
    /// <summary>Lowest allowed <c>accessVlan</c> value (AC2 example).</summary>
    public const int MinVlan = 1;

    /// <summary>Highest allowed <c>accessVlan</c> value (AC2 example: 5000 is rejected).</summary>
    public const int MaxVlan = 4094;

    /// <summary>Maximum length of a port's optional <c>description</c>.</summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>Maximum length of an optional neighbor system-name/port-id field.</summary>
    public const int MaxNeighborFieldLength = 128;

    /// <summary>
    /// Maximum size, in bytes, of a single rack YAML file's Git blob — checked BEFORE the blob's
    /// content is read into memory (NFR8), never after.
    /// </summary>
    public const int MaxFileBytes = 1_048_576; // 1 MiB

    /// <summary>
    /// Ceiling, in bytes, on the in-memory YAML document after parsing (defends against
    /// anchor/alias expansion bombs that a raw blob-size check alone would not catch, NFR8).
    /// </summary>
    public const int MaxYamlDocumentBytes = 2_097_152; // 2 MiB

    /// <summary>Maximum number of desired-state rack files processed in a single commit (NFR4).</summary>
    public const int MaxFilesPerCommit = 200;

    /// <summary>Maximum number of switches a single rack file may define.</summary>
    public const int MaxSwitchesPerRack = 64;

    /// <summary>Maximum total number of ports a single rack file may define across all its switches.</summary>
    public const int MaxPortsPerRack = 2048;

    /// <summary>Maximum length of a <c>rackSlug</c>.</summary>
    public const int MaxRackSlugLength = 64;

    /// <summary>Maximum length of a switch name/identifier within a rack file.</summary>
    public const int MaxSwitchNameLength = 64;

    /// <summary>Maximum length of a port name.</summary>
    public const int MaxPortNameLength = 64;

    /// <summary>Maximum length of a validation error's file path.</summary>
    public const int MaxFilePathLength = 512;

    /// <summary>Maximum length of a validation error's JSON-pointer-like location.</summary>
    public const int MaxLocationLength = 256;

    /// <summary>Maximum length of a validation error's human-readable message.</summary>
    public const int MaxValidationMessageLength = 1024;

    /// <summary>Maximum length of the ingestion run's captured commit message.</summary>
    public const int MaxCommitMessageLength = 2048;

    /// <summary>Maximum length of the ingestion run's operator-safe error summary.</summary>
    public const int MaxErrorSummaryLength = 2048;

    /// <summary>
    /// The current desired-state payload schema version stamped on every newly-persisted
    /// <see cref="DesiredStateVersion"/> (story #63, AC1). Bumping this is a forward-compatible signal
    /// for future readers; it does not itself trigger a migration.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Maximum length of a revision's captured git commit author name (story #63, AC1).</summary>
    public const int MaxAuthorNameLength = 256;

    /// <summary>Maximum length of a revision's captured git commit author email (story #63, AC1).</summary>
    public const int MaxAuthorEmailLength = 256;

    /// <summary>
    /// Maximum length, in characters, of a revision's serialized <c>DesiredStateJson</c> payload (story
    /// #63, AC1) — mirrors <see cref="MaxYamlDocumentBytes"/>, the ceiling already established for the
    /// parsed in-memory document this payload is materialised from.
    /// </summary>
    public const int MaxDesiredStateJsonLength = MaxYamlDocumentBytes;

    /// <summary>Maximum length of the ingesting service principal's identity string (story #63, AC1).</summary>
    public const int MaxIngestedByLength = 128;

    /// <summary>
    /// DNS-label-shaped rack slug: lowercase alphanumeric segments separated by single hyphens, no
    /// leading/trailing hyphen (mirrors the <c>desired-state/racks/&lt;rackSlug&gt;.yaml</c> path
    /// convention, AC1 examples).
    /// </summary>
    public static bool IsValidRackSlug(string value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= MaxRackSlugLength
            && RackSlugPattern().IsMatch(value);

    /// <summary>Switch/port names: printable, non-whitespace tokens commonly used as device identifiers.</summary>
    public static bool IsValidDeviceName(string value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= MaxSwitchNameLength
            && DeviceNamePattern().IsMatch(value);

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex RackSlugPattern();

    [GeneratedRegex(@"^[A-Za-z0-9/_.:-]{1,64}$")]
    private static partial Regex DeviceNamePattern();
}
