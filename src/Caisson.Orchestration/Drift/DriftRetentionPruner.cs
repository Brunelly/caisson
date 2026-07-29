using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Drift;

/// <summary>
/// Enforces the hybrid drift-report retention policy (story #64, NFR5/Q3): per rack, keep at most
/// <see cref="DriftOrchestrationOptions.RetentionMaxReportsPerRack"/> of the newest reports AND never keep
/// one older than <see cref="DriftOrchestrationOptions.RetentionMaxDays"/> — a report survives only if it
/// satisfies BOTH bounds. Deleting a <c>DriftReport</c> cascades its <c>DriftItem</c> rows via the DB FK
/// (ADR 0028), so no separate item-level delete pass is needed. Mirrors <c>DiscoveryScheduler</c>'s
/// <c>PeriodicTimer</c>/<c>TimeProvider</c> shape; each rack is pruned in its own try/catch so one rack's
/// failure never blocks the others.
/// </summary>
public sealed class DriftRetentionPruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IOptions<DriftOrchestrationOptions> _options;
    private readonly ILogger<DriftRetentionPruner> _logger;

    public DriftRetentionPruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        IOptions<DriftOrchestrationOptions> options,
        ILogger<DriftRetentionPruner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RetentionEnabled)
        {
            _logger.LogInformation("Drift retention pruner disabled by configuration.");
            return;
        }

        _logger.LogInformation("Drift retention pruner started.");
        var period = TimeSpan.FromSeconds(_options.Value.RetentionPollSeconds);
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
                _logger.LogError(ex, "Drift retention pruner tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Drift retention pruner stopped.");
    }

    /// <summary>Runs one pruning pass; internal so tests can drive a single deterministic tick.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var rackIds = await context.DriftReports.Select(r => r.RackId).Distinct().ToListAsync(cancellationToken);
        if (rackIds.Count == 0)
        {
            return;
        }

        var cutoffUtc = _time.GetUtcNow().UtcDateTime.AddDays(-_options.Value.RetentionMaxDays);
        var maxPerRack = _options.Value.RetentionMaxReportsPerRack;

        var totalPruned = 0;
        foreach (var rackId in rackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                totalPruned += await PruneRackAsync(context, rackId, cutoffUtc, maxPerRack, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Drift retention pruning failed rackId={RackId}; continuing with other racks.", rackId);
            }
        }

        if (totalPruned > 0)
        {
            _logger.LogInformation(
                "Drift retention pruner removed {TotalPruned} report(s) across {RackCount} rack(s).",
                totalPruned, rackIds.Count);
        }
    }

    /// <summary>
    /// Prunes one rack's reports. The per-rack report set is expected to stay bounded around
    /// <paramref name="maxPerRack"/> once the pruner has run at least once, so loading it in full (id +
    /// timestamp only) to decide what to delete is not the kind of unbounded query the hardening
    /// invariant guards against — it is scoped to a single rack and self-limiting by policy.
    /// </summary>
    private static async Task<int> PruneRackAsync(
        CaissonDbContext context, Guid rackId, DateTime cutoffUtc, int maxPerRack, CancellationToken cancellationToken)
    {
        var ordered = await context.DriftReports
            .Where(r => r.RackId == rackId)
            .OrderByDescending(r => r.ComputedAtUtc)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Id, r.ComputedAtUtc })
            .ToListAsync(cancellationToken);

        var staleIds = ordered.Skip(maxPerRack).Select(r => r.Id)
            .Concat(ordered.Take(maxPerRack).Where(r => r.ComputedAtUtc < cutoffUtc).Select(r => r.Id))
            .ToHashSet();

        if (staleIds.Count == 0)
        {
            return 0;
        }

        var toDelete = await context.DriftReports
            .Where(r => staleIds.Contains(r.Id))
            .ToListAsync(cancellationToken);
        context.DriftReports.RemoveRange(toDelete);
        await context.SaveChangesAsync(cancellationToken);

        return toDelete.Count;
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
