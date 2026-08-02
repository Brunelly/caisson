using System.Text.Json;
using Caisson.Domain.Auditing;
using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Infrastructure.Persistence.Queries;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// The production <see cref="IDriftApplyJobStore"/>: a thin wrapper over <see cref="CaissonDbContext"/>.
/// <see cref="SaveTerminalAsync"/> stages the job's Tier 1 audit event (story #308, ADR 0064) in the SAME
/// transaction as the terminal status.
/// </summary>
public sealed class CaissonDriftApplyJobStore : IDriftApplyJobStore
{
    /// <summary>Mirrors <c>Caisson.Api.Security.CaissonRoles.DriftApply</c>/<c>AuthorizationPolicies.DriftApply</c>
    /// (both "DriftApply"). Duplicated as a literal because Caisson.Orchestration must not reference
    /// Caisson.Api (layering rule, ADR 0013).</summary>
    private const string DriftApplyPermissionName = "DriftApply";

    private readonly CaissonDbContext _context;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly TimeProvider _time;

    public CaissonDriftApplyJobStore(CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public Task SaveTerminalAsync(DriftApplyJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!DriftApplyJobService.IsTerminal(job.Status))
        {
            throw new InvalidOperationException(
                $"Drift-apply job '{job.Id}' is not terminal (status={job.Status}); SaveTerminalAsync requires the caller to transition it first.");
        }

        StageTerminalAudit(_context, _auditOutbox, job, _time.GetUtcNow().UtcDateTime);
        return _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<DriftItem?> FindCurrentAccessVlanItemAsync(
        Guid rackId, DriftSubjectType subjectType, string subjectKey, CancellationToken cancellationToken)
        => _context.LatestItemBySubjectAsync(rackId, subjectType, subjectKey, DriftType.AccessVlanMismatch, cancellationToken);

    /// <summary>
    /// Stages the Tier 1 terminal-transition audit event for <paramref name="job"/> onto <paramref name="context"/>.
    /// Static (and internal) so the exhausted-attempt reaper's bounded reconciliation
    /// (<c>DriftApplyJobRunner.FailExhaustedStaleJobsAsync</c>) can share the exact same audit-action
    /// mapping/shape without needing a whole store instance per reconciled job.
    /// </summary>
    internal static void StageTerminalAudit(
        CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, DriftApplyJob job, DateTime nowUtc)
    {
        var action = AuditAction(job.Status);
        var envelope = new AuditEventEnvelope(
            job.ActorType, job.RequestedBy, action, "drift-apply-job", job.Id.ToString(),
            job.CorrelationId, job.Status.ToString(), RackId: job.RackId, DetailsJson: BuildTerminalAuditDetails(job));
        auditOutbox.Add(context, envelope, nowUtc, DeterministicAuditId.For(job.Id, action));
    }

    internal static string AuditAction(DriftApplyJobStatus status) => status switch
    {
        DriftApplyJobStatus.Completed => "drift.apply.job.completed",
        DriftApplyJobStatus.Failed => "drift.apply.job.failed",
        DriftApplyJobStatus.StaleDrift => "drift.apply.job.stale-drift",
        DriftApplyJobStatus.Canceled => "drift.apply.job.canceled",
        _ => "drift.apply.job.completed",
    };

    private static string? BuildTerminalAuditDetails(DriftApplyJob job)
        => JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = DriftApplyPermissionName,
            ["driftItemId"] = job.DriftItemId,
            ["switchDeviceKey"] = job.SwitchDeviceKey,
            ["portName"] = job.PortName,
            ["desiredVlanId"] = job.DesiredVlanId,
            ["deviceReasonCode"] = job.DeviceReasonCode,
            ["deviceConfirmed"] = job.DeviceConfirmed,
            ["beforeState"] = job.BeforeStateJson,
            ["afterState"] = job.AfterStateJson,
            ["errorCategory"] = job.ErrorCategory,
            ["errorCode"] = job.ErrorCode,
        });
}
