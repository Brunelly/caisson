using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Scheduling;

/// <summary>
/// The periodic discovery scheduler (story #8, AC3). On each tick it selects enabled schedules whose
/// <see cref="RackDiscoverySchedule.NextRunAtUtc"/> is due and enqueues a scheduled job through the
/// SAME <see cref="IDiscoveryJobService.EnqueueAsync"/> the trigger endpoint uses — so the
/// single-active-per-rack index makes an already-active rack a no-op. It always advances
/// <c>NextRunAtUtc = now + interval + jitter</c> and stamps <c>LastAttemptAtUtc</c>, whether or not a
/// job was created. Uses <see cref="TimeProvider"/> and an injectable <see cref="IJitterSource"/> so the
/// scheduled path is deterministic under test.
/// </summary>
public sealed class DiscoveryScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJitterSource _jitter;
    private readonly TimeProvider _time;
    private readonly IOptions<DiscoveryOrchestrationOptions> _options;
    private readonly ILogger<DiscoveryScheduler> _logger;

    public DiscoveryScheduler(
        IServiceScopeFactory scopeFactory,
        IJitterSource jitter,
        TimeProvider time,
        IOptions<DiscoveryOrchestrationOptions> options,
        ILogger<DiscoveryScheduler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.SchedulerEnabled)
        {
            _logger.LogInformation("Discovery scheduler disabled by configuration.");
            return;
        }

        _logger.LogInformation("Discovery scheduler started.");
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
                _logger.LogError(ex, "Discovery scheduler tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Discovery scheduler stopped.");
    }

    /// <summary>Runs one scheduler pass; internal so tests can drive a single deterministic tick.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IDiscoveryJobService>();

        var now = _time.GetUtcNow().UtcDateTime;
        var due = await context.RackDiscoverySchedules
            .Where(s => s.Enabled && (s.NextRunAtUtc == null || s.NextRunAtUtc <= now))
            .ToListAsync(cancellationToken);

        foreach (var schedule in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await jobs.EnqueueAsync(
                schedule.RackId, TriggerType.Scheduled, "scheduler", ActorType.System,
                Guid.NewGuid(), idempotencyKey: null, dryRun: false, cancellationToken);

            // Story #308, ADR 0064: explicitly classified as NOT an audit-worthy event, in either tier.
            // The actual scheduled job creation is already Tier 1 (staged inside EnqueueAsync above);
            // bumping the schedule's own LastAttemptAtUtc/NextRunAtUtc bookkeeping is routine per-tick
            // scheduling metadata, not a security-relevant state transition — every enabled schedule
            // advances this every ~30-60s regardless of outcome, so treating it as Tier 1 would flood the
            // outbox with system-generated noise for no security/compliance benefit.
            var nextRun = ComputeNextRun(now, schedule, _jitter);
            schedule.RecordAttempt(now, nextRun);

            _logger.LogInformation(
                "Scheduler tick rackId={RackId} disposition={Disposition} jobId={JobId} nextRunAtUtc={NextRunAtUtc}",
                schedule.RackId, result.Disposition, result.JobId, nextRun);
        }

        if (due.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Advances the next-run time by the fixed interval plus jitter (AC3). Internal so tests can assert
    /// the deterministic <c>now + interval + jitter</c> result with an injected <see cref="IJitterSource"/>.
    /// </summary>
    internal static DateTime ComputeNextRun(DateTime now, RackDiscoverySchedule schedule, IJitterSource jitter)
        => now.AddSeconds(schedule.IntervalSeconds + jitter.NextJitterSeconds(schedule.JitterSeconds));

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
