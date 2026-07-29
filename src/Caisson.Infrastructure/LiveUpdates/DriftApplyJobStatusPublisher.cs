using Microsoft.Extensions.Logging;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The single source of the fail-open drift-apply-job status publish (story #65, AC7): it allocates the
/// cluster-monotonic per-job seq (<see cref="TopologyStreams.ForDriftApplyJob"/>), builds the event,
/// publishes it, and swallows+logs any fault so a publish problem can never abort or fail a drift-apply
/// job. Mirrors <see cref="DiscoveryJobStatusPublisher"/>'s shape; shared by
/// <c>Caisson.Orchestration.DriftApply.DriftApplyJobService</c> (enqueue) and
/// <c>Caisson.Orchestration.Runner.DriftApplyJobRunner</c> (claim, revalidation and terminal transitions).
/// </summary>
public static class DriftApplyJobStatusPublisher
{
    /// <summary>Publishes a drift-apply-job status change, fail-open — never throws.</summary>
    public static async Task PublishDriftApplyJobStatusAsync(
        this ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        TimeProvider time,
        ILogger logger,
        Guid rackId,
        Guid jobId,
        string status,
        string? previousStatus,
        string? currentStep,
        string? reasonCode,
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
            var seq = await sequencer.NextAsync(TopologyStreams.ForDriftApplyJob(jobId), cancellationToken);
            var @event = new DriftApplyJobStatusChangedEvent(
                rackId, jobId, status, previousStatus, currentStep, reasonCode, errorCode,
                time.GetUtcNow(), seq, correlationId);
            await events.PublishDriftApplyJobStatusChangedAsync(@event, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "drift-apply-job-status-changed publish failed (swallowed) jobId={JobId} status={Status} correlationId={CorrelationId}",
                jobId, status, correlationId);
        }
    }
}
