using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Runner;

/// <summary>
/// The durable drift-apply job runner (story #65, AC4/AC5). Each cycle it atomically claims one Pending
/// job — or reclaims a non-terminal job whose heartbeat is stale (crashed host) — via
/// <c>UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED)</c> so multiple replicas can never
/// double-claim, then runs it through <see cref="IDriftApplyOrchestrator"/> in a fresh DI scope. A
/// terminal outcome writes a <c>drift.apply.job.*</c> audit event stamped with the job correlation id.
/// Mirrors <c>DiscoveryJobRunner</c>'s claim/reclaim/finalize shape.
/// </summary>
public sealed class DriftApplyJobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DriftApplyJobSignal _signal;
    private readonly TimeProvider _time;
    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly DriftApplyMetrics _metrics;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly IOptions<DriftApplyOrchestrationOptions> _options;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ILogger<DriftApplyJobRunner> _logger;

    public DriftApplyJobRunner(
        IServiceScopeFactory scopeFactory,
        DriftApplyJobSignal signal,
        TimeProvider time,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        DriftApplyMetrics metrics,
        IMandatoryAuditOutbox auditOutbox,
        IOptions<DriftApplyOrchestrationOptions> options,
        ILogger<DriftApplyJobRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RunnerEnabled)
        {
            _logger.LogInformation("Drift-apply job runner disabled by configuration.");
            return;
        }

        _logger.LogInformation("Drift-apply job runner started instanceId={InstanceId}.", _instanceId);
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
                _logger.LogError(ex, "Drift-apply job runner cycle failed; backing off before retrying.");
                processed = false;
            }

            if (!processed)
            {
                await WaitForWorkAsync(stoppingToken);
            }
        }

        _logger.LogInformation("Drift-apply job runner stopped.");
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

        var job = await context.DriftApplyJobs
            .Include(j => j.Steps)
            .FirstAsync(j => j.Id == claimedId, stoppingToken);

        // job.Status is already 'Claimed' — ClaimNextAsync's atomic UPDATE performed the whole
        // claim transition (status/claimed-by/heartbeat/attempt-count) in one statement, mirroring
        // DiscoveryJobRunner (no separate domain-level MarkClaimed call on the runner's hot path).
        await PublishStatusAsync(job, previousStatus: DriftApplyJobStatus.Pending.ToString(), currentStep: null, reasonCode: null, stoppingToken);

        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IDriftApplyOrchestrator>();
            await orchestrator.RunAsync(job, stoppingToken);
            await FinalizeAsync(context, job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown mid-run: leave the job non-terminal; its stale heartbeat triggers reclaim.
            _logger.LogInformation(
                "Drift-apply job interrupted by shutdown, will be reclaimed jobId={JobId}", claimedId);
        }

        return true;
    }

    private async Task<Guid?> ClaimAsync(CaissonDbContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeUtcNow;
        var stale = now.AddSeconds(-_options.Value.HeartbeatStalenessSeconds);
        var maxAttempts = _options.Value.MaxJobAttempts;

        await FailExhaustedStaleJobsAsync(context, _auditOutbox, now, stale, maxAttempts, cancellationToken);
        return await ClaimNextAsync(context, now, stale, maxAttempts, _instanceId, cancellationToken);
    }

    /// <summary>
    /// Fails any non-terminal job whose heartbeat is stale AND whose <c>attempt_count</c> has already
    /// reached <paramref name="maxAttempts"/>. Rewritten (story #308, ADR 0064) from a bulk raw-SQL
    /// <c>UPDATE</c> into a bounded, transaction-scoped reconciliation — see
    /// <c>DiscoveryJobRunner.FailExhaustedStaleJobsAsync</c>'s remarks for the full rationale (claim via
    /// <c>FOR UPDATE SKIP LOCKED</c>, domain <c>Fail</c> transition, deterministic outbox id, one
    /// transaction). Internal so concurrency tests can exercise it directly.
    /// </summary>
    internal static async Task FailExhaustedStaleJobsAsync(
        CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, DateTime now, DateTime stale, int maxAttempts,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        const string selectSql = @"
SELECT id FROM drift_apply_job
WHERE status IN ('Pending','Claimed','Revalidating','Executing')
  AND (last_heartbeat_at_utc IS NULL OR last_heartbeat_at_utc < {0})
  AND attempt_count >= {1}
ORDER BY requested_at_utc
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

        var jobs = await context.DriftApplyJobs.Where(j => ids.Contains(j.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Fail(
                now, DriftApplyErrorCategories.Infrastructure, DriftApplyErrorCodes.MaxAttemptsExceeded,
                DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.MaxAttemptsExceeded));
            CaissonDriftApplyJobStore.StageTerminalAudit(context, auditOutbox, job, now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically claims the next Pending job — or reclaims a non-terminal job whose heartbeat is older
    /// than <paramref name="stale"/> — via <c>FOR UPDATE SKIP LOCKED</c>, excluding any job that has
    /// already reached <paramref name="maxAttempts"/>. The single <c>UPDATE</c> performs the WHOLE claim
    /// transition atomically (status→Claimed, claimed-by/heartbeat/attempt-count) — mirroring
    /// <c>DiscoveryJobRunner.ClaimNextAsync</c> — so the caller never needs a second domain-level write to
    /// record the claim. Internal so concurrency tests can exercise it directly.
    /// </summary>
    internal static async Task<Guid?> ClaimNextAsync(
        CaissonDbContext context, DateTime now, DateTime stale, int maxAttempts, string instanceId, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE drift_apply_job
SET status = 'Claimed',
    claimed_at_utc = COALESCE(claimed_at_utc, {0}),
    claimed_by_instance_id = {3},
    last_heartbeat_at_utc = {0},
    attempt_count = attempt_count + 1
WHERE id = (
    SELECT id FROM drift_apply_job
    WHERE (status = 'Pending'
       OR (status IN ('Claimed','Revalidating','Executing') AND (last_heartbeat_at_utc IS NULL OR last_heartbeat_at_utc < {1})))
      AND attempt_count < {2}
    ORDER BY requested_at_utc
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
RETURNING id AS ""Value""";

        var claimed = await context.Database
            .SqlQueryRaw<Guid>(sql, now, stale, maxAttempts, instanceId)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0 ? claimed[0] : null;
    }

    private async Task FinalizeAsync(CaissonDbContext context, DriftApplyJob job, CancellationToken cancellationToken)
    {
        if (!DriftApplyJobService.IsTerminal(job.Status))
        {
            return;
        }

        // The Tier 1 (mandatory-durable) audit event was already staged and committed by the orchestrator's
        // call to IDriftApplyJobStore.SaveTerminalAsync, in the SAME transaction as the terminal status
        // itself (story #308, ADR 0064) — this method only handles logging, metrics, and the fail-open
        // realtime publish that follow.
        _logger.LogInformation(
            "Drift-apply job finalized jobId={JobId} rackId={RackId} status={Status} correlationId={CorrelationId}",
            job.Id, job.RackId, job.Status, job.CorrelationId);

        var reasonCode = job.DeviceReasonCode ?? job.ErrorCode;
        var duration = (job.FinishedAtUtc ?? DateTimeUtcNow) - job.RequestedAtUtc;
        _metrics.RecordTerminal(job.Status.ToString(), reasonCode, duration);

        await PublishStatusAsync(job, previousStatus: null, currentStep: null, reasonCode: reasonCode, cancellationToken);
    }

    private Task PublishStatusAsync(
        DriftApplyJob job, string? previousStatus, string? currentStep, string? reasonCode, CancellationToken cancellationToken)
        => _events.PublishDriftApplyJobStatusAsync(
            _sequencer, _time, _logger,
            job.RackId, job.Id, job.Status.ToString(), previousStatus, currentStep, reasonCode,
            errorCode: job.ErrorCode, job.CorrelationId, cancellationToken);

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            var readTask = _signal.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(_options.Value.RunnerPollSeconds), _time, stoppingToken);
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

    private DateTime DateTimeUtcNow => _time.GetUtcNow().UtcDateTime;
}
