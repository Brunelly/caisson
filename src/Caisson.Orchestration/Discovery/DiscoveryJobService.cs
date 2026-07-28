using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The single enqueue/claim/query implementation shared by the trigger endpoint and the scheduler. It
/// inserts a queued job with its Pending steps and translates the two partial-unique-index violations
/// into the distinct conflict (409, active job) and idempotent-replay (202, existing job) results.
/// </summary>
public sealed class DiscoveryJobService : IDiscoveryJobService
{
    internal const string ActiveJobConstraint = "ux_discovery_job_rack_active";
    internal const string IdempotencyConstraint = "ux_discovery_job_rack_idempotency_key";

    private readonly CaissonDbContext _context;
    private readonly ITopologyIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly DiscoveryJobSignal _signal;
    private readonly DiscoveryCancellationRegistry _cancellation;
    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly ILogger<DiscoveryJobService> _logger;

    public DiscoveryJobService(
        CaissonDbContext context,
        ITopologyIdGenerator ids,
        TimeProvider time,
        DiscoveryJobSignal signal,
        DiscoveryCancellationRegistry cancellation,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        ILogger<DiscoveryJobService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<EnqueueResult> EnqueueAsync(
        Guid rackId,
        TriggerType mode,
        string triggeredBy,
        ActorType actorType,
        Guid correlationId,
        string? idempotencyKey,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(triggeredBy);

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await FindByIdempotencyKeyAsync(rackId, idempotencyKey, cancellationToken);
            if (existing is { } replayId)
            {
                return new EnqueueResult(EnqueueDisposition.IdempotentReplay, replayId);
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var job = new DiscoveryJob(
            _ids.NewId(), rackId, mode, triggeredBy, actorType, correlationId, now, dryRun, idempotencyKey);
        job.SeedSteps(_ids.NewId);
        _context.DiscoveryJobs.Add(job);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            Detach(job);

            if (string.Equals(pg.ConstraintName, IdempotencyConstraint, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(idempotencyKey))
            {
                var replayId = await FindByIdempotencyKeyAsync(rackId, idempotencyKey, cancellationToken);
                if (replayId is { } id)
                {
                    return new EnqueueResult(EnqueueDisposition.IdempotentReplay, id);
                }
            }

            var activeId = await FindActiveJobIdAsync(rackId, cancellationToken);
            if (activeId is { } active)
            {
                _logger.LogInformation(
                    "Discovery enqueue conflicted, rack already active rackId={RackId} activeJobId={ActiveJobId} correlationId={CorrelationId}",
                    rackId, active, correlationId);
                return new EnqueueResult(EnqueueDisposition.Conflict, active);
            }

            throw;
        }

        _signal.Notify(job.Id);
        _logger.LogInformation(
            "Discovery job queued jobId={JobId} rackId={RackId} mode={Mode} correlationId={CorrelationId} dryRun={DryRun}",
            job.Id, rackId, mode, correlationId, dryRun);

        // Live update (story #9): the durable Created→Queued transition. Fail-open + belt-and-braces so a
        // publish fault can never fail the enqueue (AC4/NFR3).
        await PublishStatusAsync(
            rackId, job.Id, DiscoveryJobStatus.Queued.ToString(), previousStatus: null, errorCode: null,
            correlationId, cancellationToken);

        return new EnqueueResult(EnqueueDisposition.Created, job.Id);
    }

    private async Task PublishStatusAsync(
        Guid rackId, Guid jobId, string status, string? previousStatus, string? errorCode,
        Guid correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var seq = await _sequencer.NextAsync("job:" + jobId.ToString("N"), cancellationToken);
            var @event = new DiscoveryJobStatusChangedEvent(
                rackId, jobId, status, previousStatus, CurrentStep: null, errorCode,
                _time.GetUtcNow(), seq, correlationId);
            await _events.PublishJobStatusChangedAsync(@event, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "discovery-job-status-changed publish failed (swallowed) jobId={JobId} status={Status} correlationId={CorrelationId}",
                jobId, status, correlationId);
        }
    }

    /// <inheritdoc />
    public async Task<CancelResult> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.DiscoveryJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return new CancelResult(CancelDisposition.NotFound);
        }

        if (IsTerminal(job.Status))
        {
            return new CancelResult(CancelDisposition.AlreadyTerminal);
        }

        job.RequestCancellation();
        await _context.SaveChangesAsync(cancellationToken);

        _cancellation.Signal(jobId);
        _signal.Notify(jobId);
        _logger.LogInformation(
            "Discovery job cancellation requested jobId={JobId} rackId={RackId}", jobId, job.RackId);
        return new CancelResult(CancelDisposition.Requested);
    }

    /// <inheritdoc />
    public async Task<List<DiscoveryJob>> GetJobsPageAsync(
        Guid rackId, KeysetPosition? after, int limit, CancellationToken cancellationToken)
    {
        var query = _context.DiscoveryJobs.AsNoTracking().Where(j => j.RackId == rackId);
        if (after is { } cursor)
        {
            query = query.Where(j =>
                j.CreatedAtUtc < cursor.TimestampUtc
                || (j.CreatedAtUtc == cursor.TimestampUtc && j.Id < cursor.Id));
        }

        return await query
            .OrderByDescending(j => j.CreatedAtUtc)
            .ThenByDescending(j => j.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DiscoveryJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await _context.DiscoveryJobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    /// <inheritdoc />
    public async Task<DateTime?> GetLastSuccessAtUtcAsync(Guid rackId, CancellationToken cancellationToken)
        => await _context.DiscoveryJobs
            .Where(j => j.RackId == rackId && j.Status == DiscoveryJobStatus.Succeeded)
            .MaxAsync(j => (DateTime?)j.FinishedAtUtc, cancellationToken);

    /// <inheritdoc />
    public async Task<DiscoveryStatusSummary> GetStatusAsync(Guid rackId, CancellationToken cancellationToken)
    {
        var latest = await _context.DiscoveryJobs
            .AsNoTracking()
            .Where(j => j.RackId == rackId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .ThenByDescending(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var lastSuccess = await _context.DiscoveryJobs
            .Where(j => j.RackId == rackId && j.Status == DiscoveryJobStatus.Succeeded)
            .MaxAsync(j => (DateTime?)j.FinishedAtUtc, cancellationToken);

        var schedule = await _context.RackDiscoverySchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.RackId == rackId, cancellationToken);

        // Use the true max across all succeeded jobs (on-demand *and* scheduled): the schedule's
        // LastSuccessAtUtc is only stamped for scheduled runs, so it can understate the rack's actual
        // last-success time when a later on-demand run succeeds. Keeps parity with the list endpoint's
        // GetLastSuccessAtUtcAsync so the two never disagree (AC4).
        return new DiscoveryStatusSummary(
            rackId,
            latest,
            lastSuccess,
            schedule?.Enabled ?? false,
            schedule?.NextRunAtUtc);
    }

    private async Task<Guid?> FindByIdempotencyKeyAsync(
        Guid rackId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var id = await _context.DiscoveryJobs
            .Where(j => j.RackId == rackId && j.IdempotencyKey == idempotencyKey)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return id;
    }

    private async Task<Guid?> FindActiveJobIdAsync(Guid rackId, CancellationToken cancellationToken)
        => await _context.DiscoveryJobs
            .Where(j => j.RackId == rackId
                && (j.Status == DiscoveryJobStatus.Queued || j.Status == DiscoveryJobStatus.InProgress))
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private void Detach(DiscoveryJob job)
    {
        // Snapshot the steps: detaching triggers relationship fixup that mutates the job's navigation.
        foreach (var step in job.Steps.ToList())
        {
            _context.Entry(step).State = EntityState.Detached;
        }

        _context.Entry(job).State = EntityState.Detached;
    }

    internal static bool IsTerminal(DiscoveryJobStatus status)
        => status is DiscoveryJobStatus.Succeeded or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Canceled;
}
