using System.Text.Json;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Caisson.Infrastructure.Persistence.Drift;

/// <summary>
/// The only DbContext-touching piece of the drift bridge (story #64, AC3): loads a rack's active desired
/// tree and latest observed snapshot, runs the pure <see cref="DriftEngine"/>, then upserts the result —
/// find-or-insert a <see cref="DriftReport"/> keyed by the unique
/// <c>(RackId, DesiredRevisionId, ObservedSnapshotId)</c> tuple, and upserts its <see cref="DriftItem"/>
/// rows by <c>DriftItemId</c> (inserting new ones, deleting stale ones, leaving unchanged ones alone —
/// their content is exactly what the id was hashed from). Mirrors
/// <c>TopologySnapshotIngestionService</c>'s single-atomic-<c>SaveChangesAsync</c> and
/// insert-then-retry-as-update race handling. A rack lacking either input, or whose computation/persist
/// step fails, is isolated: it logs-and-skips or persists a <see cref="DriftComputationStatus.Failed"/>
/// report, and this method never throws (except <see cref="OperationCanceledException"/>) so one rack's
/// failure never aborts a caller iterating many racks.
/// </summary>
public sealed class DriftComputationService : IDriftComputationService
{
    /// <summary>The fixed service-principal identity stamped on every drift computation audit event.</summary>
    internal const string ComputingServicePrincipal = "drift-computation";

    private const int MaxPersistAttempts = 2;

    private readonly CaissonDbContext _context;
    private readonly ITopologyIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly IOptions<DriftComputationOptions> _options;
    private readonly ILogger<DriftComputationService> _logger;

    public DriftComputationService(
        CaissonDbContext context,
        ITopologyIdGenerator ids,
        TimeProvider time,
        IOptions<DriftComputationOptions> options,
        ILogger<DriftComputationService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ComputeAndPersistAsync(Guid rackId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var rack = await _context.Racks.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rackId, cancellationToken);
        if (rack is null)
        {
            _logger.LogInformation(
                "Drift compute skipped: rack not found rackId={RackId} correlationId={CorrelationId}", rackId, correlationId);
            return;
        }

        var desiredTree = await _context.ActiveVersionWithTreeAsync(rack.ExternalKey, cancellationToken);
        var observed = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        if (desiredTree is null || observed is null)
        {
            _logger.LogInformation(
                "Drift compute skipped rackId={RackId} hasDesiredRevision={HasDesired} hasObservedSnapshot={HasObserved} correlationId={CorrelationId}",
                rackId, desiredTree is not null, observed is not null, correlationId);
            return;
        }

        var desiredRevisionId = desiredTree.Version.Id;
        var observedSnapshotId = observed.Id;
        var computedAtUtc = _time.GetUtcNow().UtcDateTime;

        try
        {
            var engineInput = new DesiredStateTree(desiredTree.Version, desiredTree.Rack, desiredTree.Switches, desiredTree.Ports);
            var result = DriftEngine.Compute(engineInput, observed, rackId, computedAtUtc, _options.Value);

            await PersistWithRetryAsync(rackId, desiredRevisionId, observedSnapshotId, correlationId, computedAtUtc, result, cancellationToken);

            _logger.LogInformation(
                "Drift computed rackId={RackId} desiredRevisionId={DesiredRevisionId} observedSnapshotId={ObservedSnapshotId} " +
                "totalItems={TotalItems} hasAmbiguities={HasAmbiguities} isTruncated={IsTruncated} correlationId={CorrelationId}",
                rackId, desiredRevisionId, observedSnapshotId, result.Items.Count, result.HasAmbiguities, result.IsTruncated, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Drift computation failed rackId={RackId} desiredRevisionId={DesiredRevisionId} observedSnapshotId={ObservedSnapshotId} correlationId={CorrelationId}",
                rackId, desiredRevisionId, observedSnapshotId, correlationId);
            await PersistFailureAsync(rackId, desiredRevisionId, observedSnapshotId, correlationId, computedAtUtc, ex, cancellationToken);
        }
    }

    private async Task PersistWithRetryAsync(
        Guid rackId, Guid desiredRevisionId, Guid observedSnapshotId, Guid correlationId, DateTime computedAtUtc,
        DriftComputationResult result, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await PersistAsync(rackId, desiredRevisionId, observedSnapshotId, correlationId, computedAtUtc, result, cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxPersistAttempts && IsUniqueViolation(ex))
            {
                // Another replica raced us on the same (rack, revision, snapshot) tuple or the same
                // DriftItemId — deterministic recompute means retrying converges safely to one report.
                DetachAll();
            }
        }
    }

    private async Task PersistAsync(
        Guid rackId, Guid desiredRevisionId, Guid observedSnapshotId, Guid correlationId, DateTime computedAtUtc,
        DriftComputationResult result, CancellationToken cancellationToken)
    {
        var report = await _context.DriftReports.FirstOrDefaultAsync(
            r => r.RackId == rackId && r.DesiredRevisionId == desiredRevisionId && r.ObservedSnapshotId == observedSnapshotId,
            cancellationToken);

        var isNewReport = report is null;
        if (report is null)
        {
            report = new DriftReport(
                _ids.NewId(), rackId, desiredRevisionId, observedSnapshotId, computedAtUtc,
                DriftSchema.CurrentComputationVersion, result.Items.Count, result.CountsBySeverityJson,
                result.HasAmbiguities, result.IsTruncated, DriftComputationStatus.Succeeded);
            _context.DriftReports.Add(report);
        }
        else
        {
            report.RecordRecomputation(
                computedAtUtc, DriftSchema.CurrentComputationVersion, result.Items.Count,
                result.CountsBySeverityJson, result.HasAmbiguities, result.IsTruncated);
        }

        var existingItems = isNewReport
            ? new List<DriftItem>()
            : await _context.DriftItems.Where(i => i.DriftReportId == report.Id).ToListAsync(cancellationToken);

        var existingItemIds = existingItems.Select(i => i.DriftItemId).ToHashSet();
        var computedItemIds = result.Items.Select(i => i.DriftItemId).ToHashSet();

        foreach (var stale in existingItems.Where(i => !computedItemIds.Contains(i.DriftItemId)))
        {
            _context.DriftItems.Remove(stale);
        }

        foreach (var itemResult in result.Items)
        {
            if (existingItemIds.Contains(itemResult.DriftItemId))
            {
                continue; // Identical content already persisted — DriftItemId is a hash of that content.
            }

            _context.DriftItems.Add(new DriftItem(
                _ids.NewId(), report.Id, itemResult.DriftItemId, rackId, itemResult.DriftType, itemResult.Severity,
                itemResult.Actionable, itemResult.SubjectType, itemResult.SubjectKey, itemResult.ExpectedValue,
                itemResult.ActualValue, itemResult.Why, computedAtUtc, itemResult.DetailsJson));
        }

        _context.AuditEvents.Add(new TopologyAuditEvent(
            _ids.NewId(),
            computedAtUtc,
            ActorType.System,
            ComputingServicePrincipal,
            action: "drift.report.computed",
            targetType: "drift-report",
            correlationId: correlationId,
            result: "success",
            rackId: rackId,
            snapshotId: observedSnapshotId,
            targetId: report.Id.ToString(),
            detailsJson: BuildAuditDetails(desiredRevisionId, observedSnapshotId, report.Id, result)));

        // Single implicit transaction → all-or-nothing (mirrors TopologySnapshotIngestionService, NFR3).
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistFailureAsync(
        Guid rackId, Guid desiredRevisionId, Guid observedSnapshotId, Guid correlationId, DateTime computedAtUtc,
        Exception ex, CancellationToken cancellationToken)
    {
        DetachAll();
        try
        {
            var report = await _context.DriftReports.FirstOrDefaultAsync(
                r => r.RackId == rackId && r.DesiredRevisionId == desiredRevisionId && r.ObservedSnapshotId == observedSnapshotId,
                cancellationToken);

            var errorSummary = ex.Message;
            if (report is null)
            {
                report = new DriftReport(
                    _ids.NewId(), rackId, desiredRevisionId, observedSnapshotId, computedAtUtc,
                    DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson: "{}",
                    hasAmbiguities: false, isTruncated: false, DriftComputationStatus.Failed, errorSummary);
                _context.DriftReports.Add(report);
            }
            else
            {
                report.RecordFailure(computedAtUtc, DriftSchema.CurrentComputationVersion, errorSummary);
            }

            _context.AuditEvents.Add(new TopologyAuditEvent(
                _ids.NewId(),
                computedAtUtc,
                ActorType.System,
                ComputingServicePrincipal,
                action: "drift.report.computed",
                targetType: "drift-report",
                correlationId: correlationId,
                result: "failure",
                rackId: rackId,
                snapshotId: observedSnapshotId,
                targetId: report.Id.ToString(),
                detailsJson: JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["desiredRevisionId"] = desiredRevisionId,
                    ["observedSnapshotId"] = observedSnapshotId,
                    ["error"] = errorSummary,
                })));

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception persistEx) when (persistEx is not OperationCanceledException)
        {
            // Defensive only: even the failure-recording path must never throw out of this method (AC4
            // of story #64's orchestration task depends on that isolation).
            _logger.LogError(
                persistEx,
                "Failed to persist a drift-computation-failed report rackId={RackId} correlationId={CorrelationId}",
                rackId, correlationId);
        }
    }

    private static string BuildAuditDetails(
        Guid desiredRevisionId, Guid observedSnapshotId, Guid driftReportId, DriftComputationResult result)
        => JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["desiredRevisionId"] = desiredRevisionId,
            ["observedSnapshotId"] = observedSnapshotId,
            ["driftReportId"] = driftReportId,
            ["totalItems"] = result.Items.Count,
            ["hasAmbiguities"] = result.HasAmbiguities,
            ["isTruncated"] = result.IsTruncated,
        });

    private void DetachAll()
    {
        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

/// <summary>Computes and persists drift for one rack (story #64).</summary>
public interface IDriftComputationService
{
    /// <summary>
    /// Loads the rack's active desired-state tree and latest observed snapshot, computes drift, and
    /// upserts the resulting <c>DriftReport</c>/<c>DriftItem</c> rows. A no-op (log-and-skip) when the
    /// rack, its desired revision, or its observed snapshot cannot be resolved. Never throws (except
    /// <see cref="OperationCanceledException"/>) — engine/persistence failures are recorded as a
    /// <c>DriftComputationStatus.Failed</c> report instead.
    /// </summary>
    Task ComputeAndPersistAsync(Guid rackId, Guid correlationId, CancellationToken cancellationToken = default);
}
