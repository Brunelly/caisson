using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Drift;

/// <summary>
/// The periodic drift recompute scheduler (story #64, AC4). Each tick enumerates racks that have BOTH an
/// active desired-state revision (<see cref="LatestDesiredStateVersionQueries.LatestVersionPerRackAsync"/>
/// joined to the observed-state <c>Rack</c> registry by <c>RackSlug == ExternalKey</c>, ADR 0029) and a
/// latest observed snapshot, and enqueues each through <see cref="DriftRecomputeSignal"/> — the SAME queue
/// the event-driven triggers use, so both paths funnel through
/// <see cref="DriftRecomputeRunner"/>/<c>IDriftComputationService</c>. Each rack is evaluated in its own
/// try/catch so one rack's failure (e.g. a malformed row) never aborts the rest of the tick (AC4
/// isolation) — mirrors <c>DiscoveryScheduler</c>'s shape.
/// </summary>
public sealed class DriftScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DriftRecomputeSignal _signal;
    private readonly TimeProvider _time;
    private readonly IOptions<DriftOrchestrationOptions> _options;
    private readonly ILogger<DriftScheduler> _logger;

    public DriftScheduler(
        IServiceScopeFactory scopeFactory,
        DriftRecomputeSignal signal,
        TimeProvider time,
        IOptions<DriftOrchestrationOptions> options,
        ILogger<DriftScheduler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.SchedulerEnabled)
        {
            _logger.LogInformation("Drift scheduler disabled by configuration.");
            return;
        }

        _logger.LogInformation("Drift scheduler started.");
        var period = TimeSpan.FromSeconds(_options.Value.SchedulerPollSeconds);
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
                _logger.LogError(ex, "Drift scheduler tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Drift scheduler stopped.");
    }

    /// <summary>Runs one scheduler pass; internal so tests can drive a single deterministic tick.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var activeVersions = await context.LatestVersionPerRackAsync(cancellationToken);
        if (activeVersions.Count == 0)
        {
            return;
        }

        var slugs = activeVersions.Select(v => v.RackSlug).ToHashSet(StringComparer.Ordinal);
        var racksBySlug = await context.Racks.AsNoTracking()
            .Where(r => slugs.Contains(r.ExternalKey))
            .ToDictionaryAsync(r => r.ExternalKey, StringComparer.Ordinal, cancellationToken);

        var enqueued = 0;
        foreach (var version in activeVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!racksBySlug.TryGetValue(version.RackSlug, out var rack))
                {
                    continue; // No observed-state Rack aliased to this slug yet (ADR 0029) — nothing to join against.
                }

                if (await context.LatestSnapshotIdAsync(rack.Id, cancellationToken) is null)
                {
                    continue; // No observed snapshot yet.
                }

                _signal.Enqueue(rack.Id);
                enqueued++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Drift scheduler failed to evaluate rackSlug={RackSlug}; continuing with other racks.",
                    version.RackSlug);
            }
        }

        _logger.LogInformation(
            "Drift scheduler tick enqueued {Enqueued} of {Candidates} racks with an active desired revision.",
            enqueued, activeVersions.Count);
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
