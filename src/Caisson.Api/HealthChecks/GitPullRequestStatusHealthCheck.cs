using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Caisson.Api.HealthChecks;

/// <summary>
/// Reports the GitHub PR status poller's dependency health (story #173, Task #218, NFR3). Cloned from
/// <see cref="GitIngestionHealthCheck"/>: it NEVER makes a live GitHub request (reads only the persisted
/// status snapshot) and NEVER returns <see cref="HealthStatus.Unhealthy"/> or throws — a poller/GitHub
/// problem must not take <c>/health/ready</c> down. It reports <see cref="HealthStatus.Degraded"/> when GitHub
/// has been unreachable long enough that no successful poll is newer than <c>DegradedAfterMinutes</c> while
/// polls are actively failing; otherwise <see cref="HealthStatus.Healthy"/> with the last-sync data.
/// </summary>
public sealed class GitPullRequestStatusHealthCheck : IHealthCheck
{
    private readonly CaissonDbContext _context;
    private readonly IOptions<GitPullRequestStatusOptions> _options;
    private readonly TimeProvider _time;

    public GitPullRequestStatusHealthCheck(
        CaissonDbContext context, IOptions<GitPullRequestStatusOptions> options, TimeProvider time)
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
            var snapshot = await GitPullRequestStatusQueries.HealthSnapshotAsync(_context, cancellationToken);
            var data = new Dictionary<string, object>
            {
                ["totalRecords"] = snapshot.TotalRecords,
                ["lastSuccessfulPollAtUtc"] = snapshot.LastSuccessfulPollAtUtc?.ToString("O") ?? string.Empty,
                ["maxConsecutiveFailures"] = snapshot.MaxConsecutiveFailures,
                ["lastFailureReason"] = snapshot.LastFailureReason ?? string.Empty,
            };

            // Nothing to poll, or a recent successful poll → Healthy.
            if (snapshot.TotalRecords == 0)
            {
                return HealthCheckResult.Healthy("No pull requests to poll.", data);
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var threshold = TimeSpan.FromMinutes(_options.Value.DegradedAfterMinutes);
            var hasRecentSuccess = snapshot.LastSuccessfulPollAtUtc is { } last && now - last <= threshold;

            // Degraded only on SUSTAINED failure: GitHub unreachable long enough that no success is recent
            // AND polls are actively failing (NFR3). A newly-created, not-yet-polled record is not degraded.
            if (!hasRecentSuccess && snapshot.MaxConsecutiveFailures > 0)
            {
                return HealthCheckResult.Degraded(
                    "GitHub PR status polling has been failing without a recent success.", data: data);
            }

            return HealthCheckResult.Healthy("GitHub PR status poller reachable.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive only (NFR3): a fault here must never take /health/ready down.
            return HealthCheckResult.Degraded("Could not read PR status poller health.", ex);
        }
    }
}
