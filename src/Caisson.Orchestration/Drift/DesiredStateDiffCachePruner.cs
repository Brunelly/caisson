using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Drift;

/// <summary>
/// Enforces the impact-preview diff cache TTL (story #171, Task #197): periodically deletes cache rows whose
/// <c>ExpiresAtUtc</c> has passed. Mirrors <see cref="DriftRetentionPruner"/>'s
/// <see cref="PeriodicTimer"/>/<see cref="TimeProvider"/> shape with an internal <see cref="TickAsync"/> so
/// tests can drive a single deterministic tick against a controllable clock. Deletes only expired rows in
/// bounded batches (<see cref="DesiredStateDiffCacheOptions.PruneBatchSize"/>) via
/// <c>ExecuteDeleteAsync</c> — the cache entity is deliberately mutable (not append-only) so the
/// <c>DbContext</c> guard does not block the sweep.
/// </summary>
public sealed class DesiredStateDiffCachePruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IOptions<DesiredStateDiffCacheOptions> _options;
    private readonly ILogger<DesiredStateDiffCachePruner> _logger;

    public DesiredStateDiffCachePruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        IOptions<DesiredStateDiffCacheOptions> options,
        ILogger<DesiredStateDiffCachePruner> logger)
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
            _logger.LogInformation("Desired-state diff cache pruner disabled by configuration.");
            return;
        }

        _logger.LogInformation("Desired-state diff cache pruner started.");
        var period = TimeSpan.FromSeconds(_options.Value.PollSeconds);
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
                _logger.LogError(ex, "Desired-state diff cache pruner tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Desired-state diff cache pruner stopped.");
    }

    /// <summary>Runs one pruning pass; internal so tests can drive a single deterministic tick.</summary>
    internal async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var batchSize = _options.Value.PruneBatchSize;

        var totalPruned = 0;
        int deleted;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Bounded batch: delete the oldest-expired ids first so a large backlog drains deterministically.
            var expiredIds = await context.DesiredStateCandidateDiffCaches
                .Where(c => c.ExpiresAtUtc != null && c.ExpiresAtUtc < nowUtc)
                .OrderBy(c => c.ExpiresAtUtc)
                .ThenBy(c => c.Id)
                .Select(c => c.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (expiredIds.Count == 0)
            {
                break;
            }

            deleted = await context.DesiredStateCandidateDiffCaches
                .Where(c => expiredIds.Contains(c.Id))
                .ExecuteDeleteAsync(cancellationToken);
            totalPruned += deleted;
        }
        while (deleted == batchSize);

        if (totalPruned > 0)
        {
            _logger.LogInformation("Desired-state diff cache pruner removed {TotalPruned} expired preview(s).", totalPruned);
        }

        return totalPruned;
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
