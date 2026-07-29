using Caisson.Domain.DesiredState;

namespace Caisson.Ingestion.Schema;

/// <summary>
/// A single parse or validation problem found in one rack file, before it is known which
/// <c>DesiredStateIngestionRun</c> produced it. <c>Caisson.Ingestion</c> stays DB-free (ADR 0026), so
/// this — not the domain <see cref="DesiredStateValidationError"/> entity — is what
/// <see cref="DesiredStateYamlParser"/>/<see cref="DesiredStateValidator"/> produce; the orchestration
/// layer (story #62, step 4) attaches <c>ingestionRunId</c>/<c>rackSlug</c> and materialises the entity.
/// </summary>
public sealed record DesiredStateValidationIssue(
    string FilePath,
    string Location,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error,
    int? Line = null,
    int? Column = null);
