using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Runner;

/// <summary>
/// Drains webhook-triggered ingestion requests off <see cref="DesiredStateIngestionSignal"/> so the
/// webhook endpoint can enqueue and return <c>202 Accepted</c> immediately (story #62, AC1) while
/// processing happens reliably in-process. Deliberately NOT a resumable multi-step claim/heartbeat loop
/// like <c>DiscoveryJobRunner</c> — parsing/validating/materialising a commit is a single bounded
/// operation well under NFR4's 30s P95 budget, so there is nothing here to resume. Per-request exception
/// isolation means one bad delivery never crashes the host or blocks the next one.
/// </summary>
public sealed class DesiredStateIngestionRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DesiredStateIngestionSignal _signal;
    private readonly ILogger<DesiredStateIngestionRunner> _logger;

    public DesiredStateIngestionRunner(
        IServiceScopeFactory scopeFactory, DesiredStateIngestionSignal signal, ILogger<DesiredStateIngestionRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Desired-state webhook ingestion drainer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            WebhookIngestionRequest request;
            try
            {
                if (!await _signal.Reader.WaitToReadAsync(stoppingToken) || !_signal.Reader.TryRead(out request!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessOneAsync(request, stoppingToken);
        }

        _logger.LogInformation("Desired-state webhook ingestion drainer stopped.");
    }

    /// <summary>Processes one queued request; internal so tests can drive it deterministically.</summary>
    internal async Task ProcessOneAsync(WebhookIngestionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDesiredStateIngestionService>();

            var result = await service.RunAsync(
                IngestionTriggerType.Webhook, request.WebhookDeliveryId, request.CorrelationId, cancellationToken);

            _logger.LogInformation(
                "Webhook-triggered desired-state ingestion completed disposition={Disposition} runId={RunId} deliveryId={DeliveryId} correlationId={CorrelationId}",
                result.Disposition, result.RunId, request.WebhookDeliveryId, request.CorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown mid-run: the DB is left with whatever the run's terminal state was (or,
            // for a fetch-in-flight cancellation, no row at all) — the next poll tick or a retried
            // webhook delivery will pick the commit back up.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Webhook-triggered desired-state ingestion failed deliveryId={DeliveryId} correlationId={CorrelationId}",
                request.WebhookDeliveryId, request.CorrelationId);
        }
    }
}
