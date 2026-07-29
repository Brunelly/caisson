using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Caisson.Api.HealthChecks;

/// <summary>
/// Reports the desired-state ingestion subsystem's last-run status (story #62, NFR8) — never touches a
/// device, mirroring the safety boundary of every other check in the <c>/health/ready</c> chain. A
/// validation-failed or even infra-failed ingestion run is a normal operational outcome of correctly
/// rejecting bad input, not a service-health problem, so this check reports <see cref="HealthStatus.Healthy"/>
/// with the last run's status/time as diagnostic data in every case except an unexpected fault querying
/// the database itself (already covered by the separate "db" NpgSql check, but defended here too so a
/// fault in this check specifically can never propagate and take <c>/health/ready</c> down).
/// </summary>
public sealed class GitIngestionHealthCheck : IHealthCheck
{
    private readonly CaissonDbContext _context;

    public GitIngestionHealthCheck(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await _context.LatestIngestionRunAsync(cancellationToken);
            var lastSuccessAtUtc = await _context.LastSuccessfulIngestionAtUtcAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["lastRunStatus"] = latest?.Status.ToString() ?? "NeverRun",
                ["lastRunAtUtc"] = latest?.StartedAtUtc.ToString("O") ?? string.Empty,
                ["lastSuccessAtUtc"] = lastSuccessAtUtc?.ToString("O") ?? string.Empty,
            };

            return HealthCheckResult.Healthy("Desired-state ingestion subsystem reachable.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive only: the "db" NpgSql check already covers connectivity loss. A fault here must
            // never itself take /health/ready down (NFR8), so degrade rather than throw/Unhealthy.
            return HealthCheckResult.Degraded("Could not read desired-state ingestion status.", ex);
        }
    }
}
