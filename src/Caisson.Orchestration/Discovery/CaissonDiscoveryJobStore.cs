using Caisson.Domain.Auditing;
using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The production <see cref="IDiscoveryJobStore"/>: flushes the tracked <see cref="CaissonDbContext"/>
/// and re-reads the durable cancellation flag with a fresh scalar query (bypassing the identity map so
/// a cross-instance cancel is observed). <see cref="SaveTerminalAsync"/> stages the job's Tier 1 audit
/// event (story #308, ADR 0064) in the SAME transaction as the terminal status.
/// </summary>
public sealed class CaissonDiscoveryJobStore : IDiscoveryJobStore
{
    private readonly CaissonDbContext _context;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly TimeProvider _time;

    public CaissonDiscoveryJobStore(CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public Task SaveTerminalAsync(DiscoveryJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!DiscoveryJobService.IsTerminal(job.Status))
        {
            throw new InvalidOperationException(
                $"Discovery job '{job.Id}' is not terminal (status={job.Status}); SaveTerminalAsync requires the caller to transition it first.");
        }

        StageTerminalAudit(_context, _auditOutbox, job, _time.GetUtcNow().UtcDateTime);
        return _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken)
        => await _context.DiscoveryJobs
            .Where(j => j.Id == jobId)
            .Select(j => (bool?)j.CancellationRequested)
            .FirstOrDefaultAsync(cancellationToken) ?? false;

    /// <summary>
    /// Stages the Tier 1 terminal-transition audit event for <paramref name="job"/> onto <paramref name="context"/>.
    /// Static (and internal) so the stale/exhausted-attempt reaper's bounded reconciliation
    /// (<c>DiscoveryJobRunner.FailExhaustedStaleJobsAsync</c>) can share the exact same audit-action
    /// mapping/shape without needing a whole store instance per reconciled job.
    /// </summary>
    internal static void StageTerminalAudit(
        CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, DiscoveryJob job, DateTime nowUtc)
    {
        var action = AuditAction(job.Status);
        var envelope = new AuditEventEnvelope(
            job.ActorType, job.TriggeredBy, action, "discovery-job", job.Id.ToString(),
            job.CorrelationId, job.Status.ToString(), RackId: job.RackId, SnapshotId: job.ResultSnapshotId);
        auditOutbox.Add(context, envelope, nowUtc, DeterministicAuditId.For(job.Id, action));
    }

    internal static string AuditAction(DiscoveryJobStatus status) => status switch
    {
        DiscoveryJobStatus.Succeeded => "discovery.job.succeeded",
        DiscoveryJobStatus.Failed => "discovery.job.failed",
        DiscoveryJobStatus.Canceled => "discovery.job.canceled",
        _ => "discovery.job.completed",
    };
}
