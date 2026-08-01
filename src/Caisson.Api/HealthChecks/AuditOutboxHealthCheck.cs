using Caisson.Api.Options;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Caisson.Api.HealthChecks;

/// <summary>
/// Reports the Tier 1 audit outbox dispatcher's backlog health (story #308, ADR 0064). Cloned from
/// <see cref="GitPullRequestStatusHealthCheck"/>: a DB-only snapshot (never touches
/// <c>topology_audit_event</c> or performs dispatcher work itself) that reports
/// <see cref="HealthStatus.Degraded"/> — NEVER <see cref="HealthStatus.Unhealthy"/> — on a stale backlog or
/// any poisoned rows, since an audit backlog must never fail <c>/health/ready</c>.
/// </summary>
public sealed class AuditOutboxHealthCheck : IHealthCheck
{
    private readonly CaissonDbContext _context;
    private readonly IOptions<AuditDurabilityOptions> _options;
    private readonly TimeProvider _time;

    public AuditOutboxHealthCheck(CaissonDbContext context, IOptions<AuditDurabilityOptions> options, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await AuditOutboxQueries.HealthSnapshotAsync(_context, cancellationToken);
            var now = _time.GetUtcNow().UtcDateTime;
            var oldestPendingAgeSeconds = snapshot.OldestPendingAvailableAtUtc is { } oldest
                ? Math.Max(0, (now - oldest).TotalSeconds)
                : 0;

            var data = new Dictionary<string, object>
            {
                ["pendingCount"] = snapshot.PendingCount,
                ["oldestPendingAgeSeconds"] = oldestPendingAgeSeconds,
                ["poisonedCount"] = snapshot.PoisonedCount,
            };

            if (snapshot.PoisonedCount > 0)
            {
                return HealthCheckResult.Degraded(
                    $"{snapshot.PoisonedCount} audit outbox row(s) are poisoned and require operator triage.", data: data);
            }

            // Degraded threshold: a backlog older than several dispatch cycles suggests the dispatcher is
            // stuck (or disabled) rather than merely catching up on a burst.
            var staleThresholdSeconds = Math.Max(60, _options.Value.OutboxPollIntervalSeconds * 30);
            if (oldestPendingAgeSeconds > staleThresholdSeconds)
            {
                return HealthCheckResult.Degraded(
                    $"Oldest pending audit outbox row is {oldestPendingAgeSeconds:F0}s old.", data: data);
            }

            return HealthCheckResult.Healthy("Audit outbox dispatcher backlog is nominal.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive only: a fault here must never take /health/ready down.
            return HealthCheckResult.Degraded("Could not read audit outbox dispatcher health.", ex);
        }
    }
}
