using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// Raw-SQL persistence helpers for the PR status poller's DB-backed lease (story #173, Task #211b). The claim
/// uses the codebase's established atomic pattern <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED
/// LIMIT n) RETURNING id</c> (modelled on <c>DiscoveryJobRunner.ClaimNextAsync</c>/<c>DriftApplyJobRunner</c>):
/// two replicas can never double-claim the same PR, guaranteeing the ≤2-GitHub-calls-per-PR budget (NFR1). The
/// claim advances <c>next_poll_after_utc</c> to a short lease horizon so a PR is not re-selected mid-poll and a
/// crashed poll becomes due again after the lease expires.
/// </summary>
public static class GitPullRequestStatusQueries
{
    /// <summary>
    /// First-sights a status record (state=Open, checks=Unknown, due now) for every published, still-Open link
    /// that has no status row yet. Race-safe across replicas via <c>ON CONFLICT (pull_request_link_id) DO
    /// NOTHING</c> against the 1:1 unique index. Returns the number of rows inserted.
    /// </summary>
    public static Task<int> UpsertMissingStatusRecordsAsync(
        CaissonDbContext context, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = @"
INSERT INTO git_pull_request_status (
    id, pull_request_link_id, rack_id, repo_owner, repo_name, pull_request_number, pull_request_url,
    state, checks_conclusion, head_sha, failing_checks_count, checks_summary,
    last_checked_at_utc, next_poll_after_utc, consecutive_poll_failures, last_poll_failure_reason, updated_at_utc)
SELECT gen_random_uuid(), l.id, l.rack_id, l.repo_owner, l.repo_name, l.pull_request_number, l.pull_request_url,
    'Open', 'Unknown', NULL, NULL, NULL,
    {0}, {0}, 0, NULL, {0}
FROM git_pull_request_link l
LEFT JOIN git_pull_request_status s ON s.pull_request_link_id = l.id
WHERE l.status = 'Open'
  AND l.pull_request_number IS NOT NULL
  AND l.pull_request_url IS NOT NULL
  AND s.id IS NULL
ON CONFLICT (pull_request_link_id) DO NOTHING";

        object[] parameters = { nowUtc };
        return context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due status records (those whose owning link is still
    /// Open+published and whose <c>next_poll_after_utc</c> has elapsed), advancing each claimed row's
    /// <c>last_checked_at_utc</c> to <paramref name="nowUtc"/> and <c>next_poll_after_utc</c> to
    /// <paramref name="leaseUntilUtc"/> so no other replica re-selects it during the poll. Returns the claimed ids.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClaimDueAsync(
        CaissonDbContext context, DateTime nowUtc, DateTime leaseUntilUtc, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = @"
UPDATE git_pull_request_status
SET last_checked_at_utc = {0},
    next_poll_after_utc = {1}
WHERE id IN (
    SELECT s.id FROM git_pull_request_status s
    WHERE s.next_poll_after_utc <= {0}
      AND EXISTS (
          SELECT 1 FROM git_pull_request_link l
          WHERE l.id = s.pull_request_link_id
            AND l.status = 'Open'
            AND l.pull_request_number IS NOT NULL)
    ORDER BY s.next_poll_after_utc
    FOR UPDATE OF s SKIP LOCKED
    LIMIT {2}
)
RETURNING id AS ""Value""";

        var claimed = await context.Database
            .SqlQueryRaw<Guid>(sql, nowUtc, leaseUntilUtc, batchSize)
            .ToListAsync(cancellationToken);
        return claimed;
    }
}
