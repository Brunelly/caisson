using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Ingestion.Scheduling;

/// <summary>
/// The GitHub PR status poll trigger (story #173, Task #211b), cloned from <see cref="GitPollingBackgroundService"/>:
/// <see cref="PeriodicTimer"/> + <see cref="TimeProvider"/>, options-gated <c>Enabled</c>, and per-tick
/// exception isolation so a GitHub outage or one poisoned PR never crashes the host (NFR3). Each tick opens a DI
/// scope, mints one correlation id (as <c>DiscoveryScheduler</c>/<see cref="GitPollingBackgroundService"/> do),
/// and calls the scoped <see cref="IGitPullRequestStatusSyncService.SyncDueAsync"/>.
/// </summary>
public sealed class GitPullRequestStatusPoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IOptions<GitPullRequestStatusOptions> _options;
    private readonly ILogger<GitPullRequestStatusPoller> _logger;

    public GitPullRequestStatusPoller(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        IOptions<GitPullRequestStatusOptions> options,
        ILogger<GitPullRequestStatusPoller> logger)
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
            _logger.LogInformation("GitHub PR status polling is disabled by configuration.");
            return;
        }

        _logger.LogInformation("GitHub PR status poller started.");
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
                _logger.LogError(ex, "GitHub PR status poll tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("GitHub PR status poller stopped.");
    }

    /// <summary>Runs one poll tick; internal so tests can drive it deterministically without the timer.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IGitPullRequestStatusSyncService>();

        var correlationId = Guid.NewGuid();
        var polled = await service.SyncDueAsync(correlationId, cancellationToken);

        _logger.LogInformation(
            "GitHub PR status poll tick polled={Polled} correlationId={CorrelationId}", polled, correlationId);
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
