using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Runner;

/// <summary>
/// The durable job runner (story #8, AC1/NFR1/NFR5). Each cycle it atomically claims one Queued job — or
/// reclaims an <c>InProgress</c> job whose heartbeat is stale (crashed host) — via
/// <c>UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED)</c> so multiple replicas can never
/// double-claim, then runs it through <see cref="IDiscoveryOrchestrator"/> in a fresh DI scope. A
/// terminal outcome writes a <c>discovery.job.*</c> audit event stamped with the job correlation id.
/// </summary>
public sealed class DiscoveryJobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscoveryJobSignal _signal;
    private readonly DiscoveryCancellationRegistry _cancellation;
    private readonly TimeProvider _time;
    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly IOptions<DiscoveryOrchestrationOptions> _options;
    private readonly ILogger<DiscoveryJobRunner> _logger;

    public DiscoveryJobRunner(
        IServiceScopeFactory scopeFactory,
        DiscoveryJobSignal signal,
        DiscoveryCancellationRegistry cancellation,
        TimeProvider time,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        IOptions<DiscoveryOrchestrationOptions> options,
        ILogger<DiscoveryJobRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RunnerEnabled)
        {
            _logger.LogInformation("Discovery job runner disabled by configuration.");
            return;
        }

        _logger.LogInformation("Discovery job runner started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                processed = await ClaimAndRunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery job runner cycle failed; backing off before retrying.");
                processed = false;
            }

            if (!processed)
            {
                await WaitForWorkAsync(stoppingToken);
            }
        }

        _logger.LogInformation("Discovery job runner stopped.");
    }

    private async Task<bool> ClaimAndRunOneAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var jobId = await ClaimAsync(context, stoppingToken);
        if (jobId is not { } claimedId)
        {
            return false;
        }

        var job = await context.DiscoveryJobs
            .Include(j => j.Steps)
            .FirstAsync(j => j.Id == claimedId, stoppingToken);

        // Live update (story #9): the durable claim → InProgress transition.
        await PublishStatusAsync(job, DiscoveryJobStatus.InProgress, previousStatus: DiscoveryJobStatus.Queued.ToString(), stoppingToken);

        using var cts = _cancellation.Register(claimedId, stoppingToken);
        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IDiscoveryOrchestrator>();
            await orchestrator.RunAsync(job, cts.Token);
            await FinalizeAsync(context, job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown mid-run: leave the job InProgress; its stale heartbeat triggers reclaim.
            _logger.LogInformation(
                "Discovery job interrupted by shutdown, will be reclaimed jobId={JobId}", claimedId);
        }
        finally
        {
            _cancellation.Remove(claimedId);
        }

        return true;
    }

    private async Task<Guid?> ClaimAsync(CaissonDbContext context, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var stale = now.AddSeconds(-_options.Value.HeartbeatStalenessSeconds);
        var maxAttempts = _options.Value.MaxJobAttempts;

        // Reconcile first: a stale job that has already exhausted its attempts would otherwise sit
        // forever — excluded from the reclaim predicate below, but never itself marked terminal, since
        // nothing else observes it (finding #12).
        await FailExhaustedStaleJobsAsync(context, now, stale, maxAttempts, cancellationToken);
        return await ClaimNextAsync(context, now, stale, maxAttempts, cancellationToken);
    }

    /// <summary>
    /// Fails any <c>InProgress</c> job whose heartbeat is stale AND whose <c>attempt_count</c> has already
    /// reached <paramref name="maxAttempts"/> — the reconciliation half of finding #12's claim exclusion:
    /// once such a job stops being reclaimable it would otherwise never reach a terminal state. Internal
    /// so concurrency tests can exercise the reconciliation directly.
    /// </summary>
    internal static Task FailExhaustedStaleJobsAsync(
        CaissonDbContext context, DateTime now, DateTime stale, int maxAttempts, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE discovery_job
SET status = 'Failed',
    finished_at_utc = {0},
    last_heartbeat_at_utc = {0},
    error_code = {2},
    error_message = {3}
WHERE status = 'InProgress'
  AND (last_heartbeat_at_utc IS NULL OR last_heartbeat_at_utc < {1})
  AND attempt_count >= {4}";

        // NOTE: ExecuteSqlRawAsync's params-array overload greedily captures a trailing CancellationToken
        // as if it were another SQL parameter (it boxes to object like everything else) — it must be
        // passed via the explicit IEnumerable<object> + CancellationToken overload instead, or EF throws
        // "no store type mapping for CancellationToken" trying to bind it as a query parameter.
        object[] parameters =
        {
            now, stale,
            DiscoveryErrorCodes.MaxAttemptsExceeded,
            DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.MaxAttemptsExceeded),
            maxAttempts,
        };
        return context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Atomically claims the next Queued job — or reclaims an <c>InProgress</c> job whose heartbeat is
    /// older than <paramref name="stale"/> — via <c>FOR UPDATE SKIP LOCKED</c>, excluding any job that has
    /// already reached <paramref name="maxAttempts"/> (finding #12: bounded reclaim, mirroring the step
    /// retry cap). Internal so concurrency tests can exercise the claim directly.
    /// </summary>
    internal static async Task<Guid?> ClaimNextAsync(
        CaissonDbContext context, DateTime now, DateTime stale, int maxAttempts, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE discovery_job
SET status = 'InProgress',
    started_at_utc = COALESCE(started_at_utc, {0}),
    last_heartbeat_at_utc = {0},
    attempt_count = attempt_count + 1
WHERE id = (
    SELECT id FROM discovery_job
    WHERE (status = 'Queued'
       OR (status = 'InProgress' AND (last_heartbeat_at_utc IS NULL OR last_heartbeat_at_utc < {1})))
      AND attempt_count < {2}
    ORDER BY created_at_utc
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
RETURNING id AS ""Value""";

        var claimed = await context.Database
            .SqlQueryRaw<Guid>(sql, now, stale, maxAttempts)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0 ? claimed[0] : null;
    }

    private async Task FinalizeAsync(CaissonDbContext context, DiscoveryJob job, CancellationToken cancellationToken)
    {
        if (!DiscoveryJobService.IsTerminal(job.Status))
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        if (job.Status == DiscoveryJobStatus.Succeeded && job.Mode == TriggerType.Scheduled)
        {
            var schedule = await context.RackDiscoverySchedules
                .FirstOrDefaultAsync(s => s.RackId == job.RackId, cancellationToken);
            schedule?.RecordSuccess(job.FinishedAtUtc ?? now);
        }

        context.AuditEvents.Add(new TopologyAuditEvent(
            Guid.NewGuid(),
            now,
            job.ActorType,
            job.TriggeredBy,
            action: AuditAction(job.Status),
            targetType: "discovery-job",
            correlationId: job.CorrelationId,
            result: job.Status.ToString(),
            rackId: job.RackId,
            snapshotId: job.ResultSnapshotId,
            targetId: job.Id.ToString(),
            detailsJson: null));

        await context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Discovery job finalized jobId={JobId} rackId={RackId} status={Status} correlationId={CorrelationId}",
            job.Id, job.RackId, job.Status, job.CorrelationId);

        // Live update (story #9): the terminal transition, emitted right next to the audit write so events
        // and audit stay consistent. Carries the operator-safe error code on a failed job.
        await PublishStatusAsync(job, job.Status, previousStatus: DiscoveryJobStatus.InProgress.ToString(), cancellationToken);
    }

    private Task PublishStatusAsync(
        DiscoveryJob job, DiscoveryJobStatus status, string? previousStatus, CancellationToken cancellationToken)
        => _events.PublishJobStatusAsync(
            _sequencer, _time, _logger,
            job.RackId, job.Id, status.ToString(), previousStatus,
            errorCode: status == DiscoveryJobStatus.Failed ? job.ErrorCode : null,
            job.CorrelationId, cancellationToken);

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            var readTask = _signal.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var delayTask = Task.Delay(
                TimeSpan.FromSeconds(_options.Value.RunnerPollSeconds), _time, stoppingToken);
            await Task.WhenAny(readTask, delayTask);
            while (_signal.Reader.TryRead(out _))
            {
                // Drain coalesced nudges; the DB claim decides what actually runs.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private static string AuditAction(DiscoveryJobStatus status) => status switch
    {
        DiscoveryJobStatus.Succeeded => "discovery.job.succeeded",
        DiscoveryJobStatus.Failed => "discovery.job.failed",
        DiscoveryJobStatus.Canceled => "discovery.job.canceled",
        _ => "discovery.job.completed",
    };
}
