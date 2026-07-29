namespace Caisson.Domain.DesiredState;

/// <summary>
/// Stable machine-readable classification of why an ingestion run reached
/// <see cref="IngestionRunStatus.Failed"/> (story #62, AC6). Recorded on
/// <see cref="DesiredStateIngestionRun.ErrorCategory"/> alongside an operator-safe
/// <see cref="DesiredStateIngestionRun.ErrorSummary"/>; the stack trace itself stays in the log sink
/// only (AC6, never returned to the API/UI).
/// </summary>
public enum IngestionErrorCategory
{
    /// <summary>Could not authenticate to the configured Git repository.</summary>
    Auth = 0,

    /// <summary>A network/transport failure prevented fetching the commit or its files.</summary>
    Network,

    /// <summary>The commit's YAML could not be parsed at all (distinct from schema validation failure).</summary>
    Parse,

    /// <summary>Schema validation failed for every rack file in the commit.</summary>
    Validation,

    /// <summary>A persistence failure (other than the expected idempotent unique-violation) occurred.</summary>
    Persistence,
}
