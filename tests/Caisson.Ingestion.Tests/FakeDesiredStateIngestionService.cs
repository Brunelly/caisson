using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;

namespace Caisson.Ingestion.Tests;

/// <summary>DB-free test double recording every <see cref="RunAsync"/> call for scheduler/runner tests.</summary>
public sealed class FakeDesiredStateIngestionService : IDesiredStateIngestionService
{
    public List<(IngestionTriggerType Trigger, string? WebhookDeliveryId, Guid CorrelationId)> Calls { get; } = new();

    public Exception? ThrowOnNextCall { get; set; }

    public Task<IngestionRunResult> RunAsync(
        IngestionTriggerType trigger, string? webhookDeliveryId, Guid correlationId, CancellationToken cancellationToken)
    {
        Calls.Add((trigger, webhookDeliveryId, correlationId));

        if (ThrowOnNextCall is { } ex)
        {
            ThrowOnNextCall = null;
            throw ex;
        }

        return Task.FromResult(new IngestionRunResult(IngestionRunDisposition.Started, Guid.NewGuid()));
    }
}
