using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
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
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly IOptions<DiscoveryOrchestrationOptions> _options;
    private readonly ILogger<DiscoveryJobRunner> _logger;

    public DiscoveryJobRunner(
        IServiceScopeFactory scopeFactory,
        DiscoveryJobSignal signal,
        DiscoveryCancellationRegistry cancellation,
        TimeProvider time,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        IMandatoryAuditOutbox auditOutbox,
        IOptions<DiscoveryOrchestrationOptions> options,
        ILogger<DiscoveryJobRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
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
        await FailExhaustedStaleJobsAsync(context, _auditOutbox, now, stale, maxAttempts, cancellationToken);
        return await ClaimNextAsync(context, now, stale, maxAttempts, cancellationToken);
    }

    /// <summary>
    /// Fails any <c>InProgress</c> job whose heartbeat is stale AND whose <c>attempt_count</c> has already
    /// reached <paramref name="maxAttempts"/> — the reconciliation half of finding #12's claim exclusion:
    /// once such a job stops being reclaimable it would otherwise never reach a terminal state. Rewritten
    /// (story #308, ADR 0064) from a bulk raw-SQL <c>UPDATE</c> into a bounded, transaction-scoped
    /// reconciliation: claims candidate ids via <c>SELECT ... FOR UPDATE SKIP LOCKED</c> (same ordering as
    /// <see cref="ClaimNextAsync"/>), transitions each job through the domain <c>Fail</c> method, and
    /// stages one Tier 1 (mandatory-durable) audit event per job — all committed together in ONE
    /// transaction, so a crash mid-reconciliation leaves every job in this batch non-terminal with no
    /// orphan audit row. A deterministic outbox id (job id + terminal action) means a concurrent or
    /// retried reaper sweep can never double-stage the same job's terminal event, though the row lock
    /// already prevents two reapers from selecting the same job in the first place. Internal so
    /// concurrency tests can exercise the reconciliation directly.
    /// </summary>
    internal static async Task FailExhaustedStaleJobsAsync(
        CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, DateTime now, DateTime stale, int maxAttempts,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        const string selectSql = @"
SELECT id FROM discovery_job
WHERE status = 'InProgress'
  AND (last_heartbeat_at_utc IS NULL OR last_heartbeat_at_utc < {0})
  AND attempt_count >= {1}
ORDER BY created_at_utc
FOR UPDATE SKIP LOCKED
LIMIT {2}";

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var ids = await context.Database
            .SqlQueryRaw<Guid>(selectSql, stale, maxAttempts, batchSize)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var jobs = await context.DiscoveryJobs.Where(j => ids.Contains(j.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Fail(now, DiscoveryErrorCodes.MaxAttemptsExceeded, DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.MaxAttemptsExceeded));
            CaissonDiscoveryJobStore.StageTerminalAudit(context, auditOutbox, job, now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

        // The Tier 1 (mandatory-durable) audit event was already staged and committed by the orchestrator's
        // call to IDiscoveryJobStore.SaveTerminalAsync, in the SAME transaction as the terminal status
        // itself (story #308, ADR 0064) — this method only handles the schedule bump, logging, and the
        // fail-open realtime publish that follow.
        if (job.Status == DiscoveryJobStatus.Succeeded && job.Mode == TriggerType.Scheduled)
        {
            var schedule = await context.RackDiscoverySchedules
                .FirstOrDefaultAsync(s => s.RackId == job.RackId, cancellationToken);
            if (schedule is not null)
            {
                schedule.RecordSuccess(job.FinishedAtUtc ?? _time.GetUtcNow().UtcDateTime);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        _logger.LogInformation(
            "Discovery job finalized jobId={JobId} rackId={RackId} status={Status} correlationId={CorrelationId}",
            job.Id, job.RackId, job.Status, job.CorrelationId);

        // Live update (story #9): the terminal transition. Carries the operator-safe error code on a failed job.
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
}
