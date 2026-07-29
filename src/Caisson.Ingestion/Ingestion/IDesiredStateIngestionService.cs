using Caisson.Domain.DesiredState;

namespace Caisson.Ingestion.Ingestion;

/// <summary>Whether <see cref="IDesiredStateIngestionService.RunAsync"/> started fresh processing or found an existing run.</summary>
public enum IngestionRunDisposition
{
    /// <summary>A new run was created and processed (its final <see cref="DesiredStateIngestionRun.Status"/> may be any terminal value).</summary>
    Started,

    /// <summary>The commit (or webhook delivery id) was already processed/in-flight; the existing run is returned untouched (NFR2/NFR3).</summary>
    IdempotentReplay,
}

/// <summary>The outcome of one <see cref="IDesiredStateIngestionService.RunAsync"/> call.</summary>
public sealed record IngestionRunResult(IngestionRunDisposition Disposition, Guid RunId);

/// <summary>
/// The single shared entry point both the poll scheduler and the webhook endpoint call (story #62,
/// NFR3) — idempotent per commit SHA and per webhook delivery id, enforced at the database level by the
/// two partial-unique indexes on <c>desired_state_ingestion_run</c> so a concurrent poll+webhook for the
/// same commit can never double-process it.
/// </summary>
public interface IDesiredStateIngestionService
{
    /// <summary>
    /// Fetches the latest commit, and — unless it (or <paramref name="webhookDeliveryId"/>) was already
    /// processed — validates and materialises every matching rack file, partial-accepting per rack
    /// (Q3): a rack that fails validation keeps its previous active version and gets validation-error
    /// rows; the run's <see cref="DesiredStateIngestionRun.Status"/> reflects the aggregate outcome.
    /// </summary>
    Task<IngestionRunResult> RunAsync(
        IngestionTriggerType trigger, string? webhookDeliveryId, Guid correlationId,
        CancellationToken cancellationToken);
}
