using Caisson.Infrastructure.Persistence.Drift;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.Orchestration.Drift;

/// <summary>
/// Drains rack ids off <see cref="DriftRecomputeSignal"/> — enqueued from both the observed-snapshot and
/// desired-revision event hooks — and computes drift for each through the SAME
/// <see cref="IDriftComputationService"/> entry point the scheduler uses (story #64, AC4). Mirrors
/// <c>DesiredStateIngestionRunner</c>: not a resumable multi-step claim/heartbeat loop like
/// <c>DiscoveryJobRunner</c>, since one rack's compute-and-persist is a single bounded operation. Per-rack
/// exception isolation means one bad recompute never crashes the host or blocks the next rack in the
/// queue.
/// </summary>
public sealed class DriftRecomputeRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DriftRecomputeSignal _signal;
    private readonly ILogger<DriftRecomputeRunner> _logger;

    public DriftRecomputeRunner(
        IServiceScopeFactory scopeFactory, DriftRecomputeSignal signal, ILogger<DriftRecomputeRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Drift recompute drainer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid rackId;
            try
            {
                if (!await _signal.Reader.WaitToReadAsync(stoppingToken) || !_signal.Reader.TryRead(out rackId))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessOneAsync(rackId, stoppingToken);
        }

        _logger.LogInformation("Drift recompute drainer stopped.");
    }

    /// <summary>Processes one queued rack; internal so tests can drive it deterministically.</summary>
    internal async Task ProcessOneAsync(Guid rackId, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDriftComputationService>();
            await service.ComputeAndPersistAsync(rackId, correlationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown mid-compute: the next event enqueue or scheduler sweep will retry this rack.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Event-triggered drift recompute failed rackId={RackId} correlationId={CorrelationId}",
                rackId, correlationId);
        }
    }
}
