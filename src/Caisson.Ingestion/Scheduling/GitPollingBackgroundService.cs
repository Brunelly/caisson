using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Ingestion.Scheduling;

/// <summary>
/// The poll trigger for desired-state ingestion (story #62, AC1). On each tick it calls the SAME
/// <see cref="IDesiredStateIngestionService.RunAsync"/> entry point the webhook uses, so a commit already
/// processed by a webhook delivery is a safe no-op here too. Modelled on
/// <c>Caisson.Orchestration.Scheduling.DiscoveryScheduler</c>: <see cref="PeriodicTimer"/> +
/// <see cref="TimeProvider"/>, options-gated, per-tick exception isolation so a bad commit or a
/// transient Git fault never crashes the host.
/// </summary>
public sealed class GitPollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IOptions<GitIngestionOptions> _options;
    private readonly ILogger<GitPollingBackgroundService> _logger;

    public GitPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        IOptions<GitIngestionOptions> options,
        ILogger<GitPollingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Git desired-state ingestion is disabled by configuration.");
            return;
        }

        _logger.LogInformation("Git desired-state ingestion poll scheduler started.");
        var period = TimeSpan.FromSeconds(_options.Value.PollIntervalSeconds);
        using var timer = new PeriodicTimer(period, _time);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git desired-state ingestion poll tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Git desired-state ingestion poll scheduler stopped.");
    }

    /// <summary>Runs one poll tick; internal so tests can drive it deterministically without the timer.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDesiredStateIngestionService>();

        var correlationId = Guid.NewGuid();
        var result = await service.RunAsync(IngestionTriggerType.Poll, webhookDeliveryId: null, correlationId, cancellationToken);

        _logger.LogInformation(
            "Git desired-state ingestion poll tick disposition={Disposition} runId={RunId} correlationId={CorrelationId}",
            result.Disposition, result.RunId, correlationId);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
