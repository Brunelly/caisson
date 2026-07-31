using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// The scoped worker the poller invokes each tick (story #173, Task #211b). It first-sights status rows for
/// newly-published links, atomically claims a bounded batch of due PRs via the DB lease
/// (<see cref="GitPullRequestStatusQueries"/>), and for each claimed PR makes EXACTLY the two GitHub read
/// calls (PR, then check-runs for the PR's head SHA), applies the observation, dual-writes the link status on
/// Merge/Close in the same transaction, and hands meaningful transitions to <see cref="IPrStatusTransitionService"/>.
/// Every per-PR fault is isolated so one poisoned PR never aborts the batch (NFR3).
/// </summary>
public interface IGitPullRequestStatusSyncService
{
    /// <summary>Runs one sync pass over the currently-due PRs. Returns the number of PRs polled this pass.</summary>
    Task<int> SyncDueAsync(Guid correlationId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class GitPullRequestStatusSyncService : IGitPullRequestStatusSyncService
{
    private readonly CaissonDbContext _context;
    private readonly IGitHubPullRequestStatusClient _gitHub;
    private readonly IPrStatusTransitionService _transitions;
    private readonly IOptions<GitPullRequestStatusOptions> _options;
    private readonly TimeProvider _time;
    private readonly GitPullRequestStatusMetrics _metrics;
    private readonly ILogger<GitPullRequestStatusSyncService> _logger;

    public GitPullRequestStatusSyncService(
        CaissonDbContext context,
        IGitHubPullRequestStatusClient gitHub,
        IPrStatusTransitionService transitions,
        IOptions<GitPullRequestStatusOptions> options,
        TimeProvider time,
        GitPullRequestStatusMetrics metrics,
        ILogger<GitPullRequestStatusSyncService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> SyncDueAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var now = _time.GetUtcNow().UtcDateTime;

        await GitPullRequestStatusQueries.UpsertMissingStatusRecordsAsync(_context, now, cancellationToken);

        var leaseUntil = now.AddSeconds(options.LeaseSeconds);
        var claimedIds = await GitPullRequestStatusQueries.ClaimDueAsync(
            _context, now, leaseUntil, options.BatchSize, cancellationToken);

        _metrics.RecordRowsClaimed(claimedIds.Count);
        if (claimedIds.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "PR status poll claimed {Count} due pull request(s). correlationId={CorrelationId}",
            claimedIds.Count, correlationId);

        var polled = 0;
        foreach (var id in claimedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PollOneAsync(id, options, correlationId, cancellationToken);
                polled++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-PR isolation: one poisoned PR must never abort the batch or crash the host (NFR3).
                _logger.LogError(
                    ex, "PR status poll for record {RecordId} threw unexpectedly; skipping. correlationId={CorrelationId}",
                    id, correlationId);
                _context.ChangeTracker.Clear();
            }
        }

        return polled;
    }

    private async Task PollOneAsync(
        Guid recordId, GitPullRequestStatusOptions options, Guid correlationId, CancellationToken cancellationToken)
    {
        var record = await _context.GitPullRequestStatuses.FirstOrDefaultAsync(x => x.Id == recordId, cancellationToken);
        if (record is null)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        _metrics.RecordPollAttempt();
        var startedAt = _time.GetTimestamp();

        GitHubPullRequestSnapshot prSnapshot;
        GitHubChecksSummary checks;
        try
        {
            _metrics.RecordGitHubCall();
            prSnapshot = await _gitHub.GetPullRequestAsync(record.PullRequestNumber, cancellationToken);

            if (string.IsNullOrEmpty(prSnapshot.HeadSha))
            {
                checks = new GitHubChecksSummary(GitPullRequestChecksConclusion.Unknown, null, "{}");
            }
            else
            {
                _metrics.RecordGitHubCall();
                checks = GitHubChecksRollup.Summarize(
                    await _gitHub.GetCheckRunsForRefAsync(prSnapshot.HeadSha, cancellationToken));
            }
        }
        catch (GitHubStatusApiException ex)
        {
            var reason = GitPrPollFailureReasons.FromCategory(ex.Category);
            _metrics.RecordPollFailure(reason, _time.GetElapsedTime(startedAt));
            await RecordFailureAsync(record, ex, options, now, cancellationToken);
            return;
        }

        _metrics.RecordPollSuccess(_time.GetElapsedTime(startedAt));
        _metrics.RecordSuccessfulContact(now);

        var newState = MapState(prSnapshot);
        var previous = new PrStatusTransitionSnapshot(record.State, record.ChecksConclusion);

        var meaningful = record.ApplyObservation(
            newState, prSnapshot.HeadSha, checks.Conclusion, checks.FailingChecksCount, checks.Json, now);
        record.RecordPollSuccess(now.AddSeconds(options.PollIntervalSeconds));

        // Dual-write: once a PR is Merged/Closed, free story #172 fingerprint-reuse in the SAME transaction.
        if (newState is GitPullRequestStatus.Merged or GitPullRequestStatus.Closed)
        {
            var link = await _context.GitPullRequestLinks
                .FirstOrDefaultAsync(x => x.Id == record.PullRequestLinkId, cancellationToken);
            if (link is not null && link.Status != newState)
            {
                link.UpdateStatus(newState, now);
            }
        }

        if (meaningful)
        {
            _metrics.RecordTransition();
            // Choke point: audit + persist (status/link/audit) in one unit of work, then fail-open publish.
            await _transitions.OnStatusChangedAsync(_context, record, previous, correlationId, cancellationToken);
        }
        else
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RecordFailureAsync(
        GitPullRequestStatusRecord record,
        GitHubStatusApiException ex,
        GitPullRequestStatusOptions options,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var reason = GitPrPollFailureReasons.FromCategory(ex.Category);
        var nextPollAfter = ComputeNextPollAfter(record.ConsecutivePollFailures + 1, ex, options, now);

        record.RecordPollFailure(reason, nextPollAfter, now);

        _logger.LogWarning(
            "PR status poll failed for PR #{Number} reason={Reason} nextPollAfter={NextPoll}.",
            record.PullRequestNumber, reason, nextPollAfter);

        // A transient poll failure produces NO audit and NO event (only the choke point does), so a plain save
        // persists the sanitized failure + backoff schedule and keeps the last-known status visible.
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Schedules the next poll: a 429 honours <c>Retry-After</c>/<c>X-RateLimit-Reset</c>; every other failure
    /// gets capped exponential backoff with jitter so a persistent fault never hammers GitHub (NFR1).
    /// </summary>
    private DateTime ComputeNextPollAfter(
        int consecutiveFailures, GitHubStatusApiException ex, GitPullRequestStatusOptions options, DateTime now)
    {
        if (ex.Category == GitHubStatusFailureCategory.RateLimited)
        {
            if (ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
            {
                return now.Add(retryAfter);
            }

            if (ex.RateLimitResetUtc is { } reset && reset.UtcDateTime > now)
            {
                return reset.UtcDateTime;
            }
        }

        return now.AddSeconds(BackoffSeconds(consecutiveFailures, options));
    }

    private double BackoffSeconds(int consecutiveFailures, GitPullRequestStatusOptions options)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 16); // guard against overflow on a long outage
        var baseDelay = Math.Min(
            options.MaxBackoffSeconds,
            options.PollIntervalSeconds * Math.Pow(2, exponent));

        // Full jitter in [0, 25%] so replicas don't retry in lockstep.
        var jitter = Random.Shared.NextDouble() * baseDelay * 0.25;
        return Math.Min(options.MaxBackoffSeconds, baseDelay + jitter);
    }

    /// <summary>Maps a GitHub PR snapshot to the domain state via the explicit <c>merged</c> field.</summary>
    private static GitPullRequestStatus MapState(GitHubPullRequestSnapshot snapshot)
    {
        if (snapshot.Merged)
        {
            return GitPullRequestStatus.Merged;
        }

        return string.Equals(snapshot.State, "open", StringComparison.OrdinalIgnoreCase)
            ? GitPullRequestStatus.Open
            : GitPullRequestStatus.Closed;
    }
}
