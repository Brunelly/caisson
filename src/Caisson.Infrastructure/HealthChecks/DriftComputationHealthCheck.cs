using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Caisson.Infrastructure.HealthChecks;

/// <summary>
/// Reports the drift computation subsystem's last-run status (story #64) — never touches a device,
/// mirroring <c>GitIngestionHealthCheck</c>'s safety boundary and philosophy exactly. A failed
/// computation for one rack (recorded as a <c>DriftComputationStatus.Failed</c> report, ADR 0028) is a
/// normal, isolated operational outcome, not a service-health problem, so this check reports
/// <see cref="HealthStatus.Healthy"/> with the last run's status/time as diagnostic data in every case
/// except an unexpected fault querying the database itself — already covered by the separate "db"
/// NpgSql check, but defended here too so a fault in this check specifically can never propagate and
/// take <c>/health/ready</c> down. Dashboards/alerts on this data are an operational follow-up, consistent
/// with the repo's current bespoke-metrics scope (RouterOsMetrics/RedfishMetrics) rather than a new
/// metrics dependency.
/// </summary>
public sealed class DriftComputationHealthCheck : IHealthCheck
{
    private readonly CaissonDbContext _context;

    public DriftComputationHealthCheck(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await _context.LatestReportAcrossRacksAsync(cancellationToken);
            var lastSuccessAtUtc = await _context.LastSuccessfulComputationAtUtcAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["lastRunStatus"] = latest?.Status.ToString() ?? "NeverRun",
                ["lastRunAtUtc"] = latest?.ComputedAtUtc.ToString("O") ?? string.Empty,
                ["lastSuccessAtUtc"] = lastSuccessAtUtc?.ToString("O") ?? string.Empty,
            };

            return HealthCheckResult.Healthy("Drift computation subsystem reachable.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive only: the "db" NpgSql check already covers connectivity loss. A fault here must
            // never itself take /health/ready down, so degrade rather than throw/Unhealthy.
            return HealthCheckResult.Degraded("Could not read drift computation status.", ex);
        }
    }
}
