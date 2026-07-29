using System.Globalization;
using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// The single request-apply/claim/query implementation (story #65, AC1/AC5). Inserts a Pending job with
/// its Revalidation/DeviceApply steps and translates the active-job partial-unique-index violation into
/// the idempotent <see cref="RequestApplyDisposition.ExistingActiveJob"/> result — mirrors
/// <c>Discovery.DiscoveryJobService</c>'s enqueue/conflict-translation shape.
/// </summary>
public sealed class DriftApplyJobService : IDriftApplyJobService
{
    internal const string ActiveJobConstraint = "ux_drift_apply_job_drift_item_active";

    private readonly CaissonDbContext _context;
    private readonly ITopologyIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly DriftApplyJobSignal _signal;
    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly ILogger<DriftApplyJobService> _logger;

    public DriftApplyJobService(
        CaissonDbContext context,
        ITopologyIdGenerator ids,
        TimeProvider time,
        DriftApplyJobSignal signal,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        ILogger<DriftApplyJobService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RequestApplyResult> RequestApplyAsync(
        DriftItem item, string requestedBy, ActorType actorType, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(requestedBy);

        var now = _time.GetUtcNow().UtcDateTime;
        var job = new DriftApplyJob(
            _ids.NewId(), item.RackId, item.DriftItemId, requestedBy, actorType, correlationId, now,
            item.DriftReportId, ParseExpectedBeforeVlan(item), ParseExpectedAfterVlan(item));
        job.SeedSteps(_ids.NewId);
        _context.DriftApplyJobs.Add(job);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(pg.ConstraintName, ActiveJobConstraint, StringComparison.Ordinal))
        {
            Detach(job);

            var activeId = await FindActiveJobIdAsync(item.RackId, item.DriftItemId, cancellationToken);
            if (activeId is { } active)
            {
                _logger.LogInformation(
                    "Drift-apply request-apply found an existing active job rackId={RackId} driftItemId={DriftItemId} " +
                    "existingJobId={ExistingJobId} correlationId={CorrelationId}",
                    item.RackId, item.DriftItemId, active, correlationId);
                return new RequestApplyResult(RequestApplyDisposition.ExistingActiveJob, active);
            }

            throw;
        }

        _signal.Notify(job.Id);
        _logger.LogInformation(
            "Drift-apply job queued jobId={JobId} rackId={RackId} driftItemId={DriftItemId} correlationId={CorrelationId}",
            job.Id, item.RackId, item.DriftItemId, correlationId);

        // Live update (story #65, AC7): the created→Pending transition. Fail-open so a publish fault can
        // never fail the request-apply call.
        await _events.PublishDriftApplyJobStatusAsync(
            _sequencer, _time, _logger,
            item.RackId, job.Id, DriftApplyJobStatus.Pending.ToString(), previousStatus: null,
            currentStep: null, reasonCode: null, errorCode: null, correlationId, cancellationToken);

        return new RequestApplyResult(RequestApplyDisposition.Created, job.Id);
    }

    /// <inheritdoc />
    public async Task<DriftApplyJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await _context.DriftApplyJobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    /// <inheritdoc />
    public async Task<List<DriftApplyJob>> GetJobsPageAsync(
        Guid rackId, DriftApplyJobStatus? status, KeysetPosition? after, int limit, CancellationToken cancellationToken)
    {
        var query = _context.DriftApplyJobs.AsNoTracking().Where(j => j.RackId == rackId);
        if (status is { } filter)
        {
            query = query.Where(j => j.Status == filter);
        }

        if (after is { } cursor)
        {
            query = query.Where(j =>
                j.RequestedAtUtc < cursor.TimestampUtc
                || (j.RequestedAtUtc == cursor.TimestampUtc && j.Id < cursor.Id));
        }

        return await query
            .OrderByDescending(j => j.RequestedAtUtc)
            .ThenByDescending(j => j.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    private async Task<Guid?> FindActiveJobIdAsync(Guid rackId, Guid driftItemId, CancellationToken cancellationToken)
        => await _context.DriftApplyJobs
            .Where(j => j.RackId == rackId && j.DriftItemId == driftItemId
                && j.Status != DriftApplyJobStatus.Completed && j.Status != DriftApplyJobStatus.Failed
                && j.Status != DriftApplyJobStatus.StaleDrift && j.Status != DriftApplyJobStatus.Canceled)
            .OrderBy(j => j.RequestedAtUtc)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private void Detach(DriftApplyJob job)
    {
        // Snapshot the steps: detaching triggers relationship fixup that mutates the job's navigation.
        foreach (var step in job.Steps.ToList())
        {
            _context.Entry(step).State = EntityState.Detached;
        }

        _context.Entry(job).State = EntityState.Detached;
    }

    private static int? ParseExpectedBeforeVlan(DriftItem item)
        => int.TryParse(item.ActualValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlan)
            ? vlan
            : null;

    private static int ParseExpectedAfterVlan(DriftItem item)
        => int.TryParse(item.ExpectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlan)
            ? vlan
            : throw new InvalidOperationException(
                $"Drift item '{item.DriftItemId}' has a non-numeric ExpectedValue ('{item.ExpectedValue}') and cannot be applied.");

    internal static bool IsTerminal(DriftApplyJobStatus status)
        => status is DriftApplyJobStatus.Completed or DriftApplyJobStatus.Failed
            or DriftApplyJobStatus.StaleDrift or DriftApplyJobStatus.Canceled;
}
