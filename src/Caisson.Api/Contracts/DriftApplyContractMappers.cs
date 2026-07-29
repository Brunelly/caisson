using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;

namespace Caisson.Api.Contracts;

/// <summary>
/// Maps drift-apply domain entities to the API contracts. By construction these mappers surface only
/// status/timing/reason-code/before-after-VLAN fields — never a credentials ref, host, port, or raw
/// device diagnostics (AC6/NFR4).
/// </summary>
public static class DriftApplyContractMappers
{
    /// <summary>Maps a job to its listing summary.</summary>
    public static DriftApplyJobSummaryDto ToSummary(DriftApplyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new DriftApplyJobSummaryDto(
            job.Id,
            job.RackId,
            job.DriftItemId,
            job.Status.ToString(),
            job.RequestedAtUtc,
            job.FinishedAtUtc,
            job.RequestedBy,
            job.ErrorCategory,
            job.ErrorCode);
    }

    /// <summary>Maps a job (with steps) to its detailed progress view.</summary>
    public static DriftApplyJobDetailDto ToDetail(DriftApplyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var steps = job.Steps
            .OrderBy(s => s.StepName)
            .Select(ToStep)
            .ToList();

        return new DriftApplyJobDetailDto(
            job.Id,
            job.RackId,
            job.DriftItemId,
            job.Status.ToString(),
            job.RequestedAtUtc,
            job.ClaimedAtUtc,
            job.FinishedAtUtc,
            job.RequestedBy,
            job.ActorType.ToString(),
            job.CorrelationId,
            job.AttemptCount,
            CurrentStep(job)?.ToString(),
            job.SwitchDeviceKey,
            job.PortName,
            job.DesiredVlanId,
            job.DeviceReasonCode,
            job.DeviceConfirmed,
            job.BeforeStateJson,
            job.AfterStateJson,
            job.ErrorCategory,
            job.ErrorCode,
            job.ErrorMessage,
            steps);
    }

    private static DriftApplyStepDto ToStep(DriftApplyJobStep step)
        => new(
            step.StepName.ToString(),
            step.Status.ToString(),
            step.AttemptCount,
            step.StartedAtUtc,
            step.FinishedAtUtc,
            step.DurationMs,
            step.ErrorCode,
            step.ErrorMessage);

    private static DriftApplyStepName? CurrentStep(DriftApplyJob job)
    {
        var inProgress = job.Steps
            .Where(s => s.Status == DriftApplyStepStatus.InProgress)
            .Select(s => (DriftApplyStepName?)s.StepName)
            .OrderBy(s => s)
            .FirstOrDefault();
        if (inProgress is not null)
        {
            return inProgress;
        }

        return job.Steps
            .Where(s => s.Status == DriftApplyStepStatus.Pending)
            .Select(s => (DriftApplyStepName?)s.StepName)
            .OrderBy(s => s)
            .FirstOrDefault();
    }
}
