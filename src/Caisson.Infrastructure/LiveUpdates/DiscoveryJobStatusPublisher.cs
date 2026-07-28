using Microsoft.Extensions.Logging;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The single source of the fail-open discovery-job status publish (story #9, ADR 0014): it allocates
/// the cluster-monotonic per-job seq (<see cref="TopologyStreams.ForJob"/>), builds the event, publishes
/// it, and swallows+logs any fault so a publish problem can never abort or fail a discovery job
/// (AC4/NFR3). Shared by <c>DiscoveryJobService</c> (enqueue) and <c>DiscoveryJobRunner</c> (claim and
/// terminal transitions) so the seq key, the fail-open wrapping and the log message stay in one place.
/// </summary>
public static class DiscoveryJobStatusPublisher
{
    /// <summary>Publishes a discovery-job status change, fail-open — never throws.</summary>
    public static async Task PublishJobStatusAsync(
        this ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        TimeProvider time,
        ILogger logger,
        Guid rackId,
        Guid jobId,
        string status,
        string? previousStatus,
        string? errorCode,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var seq = await sequencer.NextAsync(TopologyStreams.ForJob(jobId), cancellationToken);
            var @event = new DiscoveryJobStatusChangedEvent(
                rackId, jobId, status, previousStatus, CurrentStep: null, errorCode,
                time.GetUtcNow(), seq, correlationId);
            await events.PublishJobStatusChangedAsync(@event, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "discovery-job-status-changed publish failed (swallowed) jobId={JobId} status={Status} correlationId={CorrelationId}",
                jobId, status, correlationId);
        }
    }
}
