using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Orchestration.Discovery;

namespace Caisson.Api.Contracts;

/// <summary>
/// Maps discovery domain entities to the API contracts. By construction these mappers surface only
/// status/timing/error/provenance fields — never a credentials ref, host, port, or raw device data (AC4).
/// </summary>
public static class DiscoveryContractMappers
{
    /// <summary>Maps a job to its history summary, carrying the rack's last-success time (AC4).</summary>
    public static DiscoveryJobSummaryDto ToSummary(DiscoveryJob job, DateTime? lastSuccessAtUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new DiscoveryJobSummaryDto(
            job.Id,
            job.RackId,
            job.Mode.ToString(),
            job.Status.ToString(),
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc,
            job.TriggeredBy,
            job.DryRun,
            job.ErrorCode,
            lastSuccessAtUtc);
    }

    /// <summary>Maps a job (with steps) to its detailed progress view (AC4).</summary>
    public static DiscoveryJobDetailDto ToDetail(DiscoveryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var steps = job.Steps
            .OrderBy(s => s.StepName)
            .Select(ToStep)
            .ToList();

        return new DiscoveryJobDetailDto(
            job.Id,
            job.RackId,
            job.Mode.ToString(),
            job.Status.ToString(),
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc,
            job.TriggeredBy,
            job.ActorType.ToString(),
            job.DryRun,
            job.CorrelationId,
            job.AttemptCount,
            CurrentStep(job)?.ToString(),
            job.ErrorCode,
            job.ErrorMessage,
            job.ResultSnapshotId,
            steps);
    }

    /// <summary>Maps a rack status summary (AC4).</summary>
    public static DiscoveryStatusDto ToStatus(DiscoveryStatusSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new DiscoveryStatusDto(
            summary.RackId,
            summary.LatestJob is null ? null : ToSummary(summary.LatestJob, summary.LastSuccessAtUtc),
            summary.LastSuccessAtUtc,
            summary.ScheduleEnabled,
            summary.NextRunAtUtc);
    }

    /// <summary>Maps a schedule to its view/response (AC3/AC4).</summary>
    public static DiscoveryScheduleDto ToSchedule(RackDiscoverySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return new DiscoveryScheduleDto(
            schedule.RackId,
            schedule.Enabled,
            schedule.IntervalSeconds,
            schedule.JitterSeconds,
            schedule.NextRunAtUtc,
            schedule.LastAttemptAtUtc,
            schedule.LastSuccessAtUtc);
    }

    private static DiscoveryStepDto ToStep(DiscoveryJobStep step)
        => new(
            step.StepName.ToString(),
            step.Status.ToString(),
            step.AttemptCount,
            step.StartedAtUtc,
            step.FinishedAtUtc,
            step.DurationMs,
            step.ErrorCode,
            step.ErrorMessage);

    private static DiscoveryStepName? CurrentStep(DiscoveryJob job)
    {
        var inProgress = job.Steps
            .Where(s => s.Status == DiscoveryStepStatus.InProgress)
            .Select(s => (DiscoveryStepName?)s.StepName)
            .OrderBy(s => s)
            .FirstOrDefault();
        if (inProgress is not null)
        {
            return inProgress;
        }

        return job.Steps
            .Where(s => s.Status == DiscoveryStepStatus.Pending)
            .Select(s => (DiscoveryStepName?)s.StepName)
            .OrderBy(s => s)
            .FirstOrDefault();
    }
}
