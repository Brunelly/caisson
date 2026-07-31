using Caisson.Domain.Git;
using Microsoft.Extensions.Logging;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The single source of the fail-open PR-status publish (story #173, Task #212), modelled on
/// <see cref="DriftApplyJobStatusPublisher"/>: it allocates the cluster-monotonic per-link seq
/// (<see cref="TopologyStreams.ForPullRequest"/>), builds the <see cref="GitPullRequestStatusChangedEvent"/>
/// from the persisted status record, publishes it, and swallows+logs any fault so a Redis problem can never
/// throw back into the poller (the post-commit/pre-publish loss window is covered by REST reconciliation).
/// </summary>
public static class GitPullRequestStatusPublisher
{
    /// <summary>Publishes a PR status change, fail-open — never throws.</summary>
    public static async Task PublishGitPullRequestStatusChangedAsync(
        this ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        ILogger logger,
        GitPullRequestStatusRecord record,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            var seq = await sequencer.NextAsync(TopologyStreams.ForPullRequest(record.PullRequestLinkId), cancellationToken);
            var @event = new GitPullRequestStatusChangedEvent(
                record.RackId,
                record.RepoOwner,
                record.RepoName,
                record.PullRequestNumber,
                record.PullRequestUrl,
                record.State.ToString(),
                record.HeadSha,
                record.ChecksConclusion.ToString(),
                record.FailingChecksCount,
                AsUtc(record.UpdatedAtUtc),
                AsUtc(record.LastCheckedAtUtc),
                seq,
                correlationId);
            await events.PublishGitPullRequestStatusChangedAsync(@event, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "git-pr-status-changed publish failed (swallowed) prNumber={Number} rackId={RackId} correlationId={CorrelationId}",
                record.PullRequestNumber, record.RackId, correlationId);
        }
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
