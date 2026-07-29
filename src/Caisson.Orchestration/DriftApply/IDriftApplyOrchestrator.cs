using Caisson.Domain.Drift.Apply;

namespace Caisson.Orchestration.DriftApply;

/// <summary>Runs a claimed <see cref="DriftApplyJob"/> through revalidation and, if still current, device apply (story #65).</summary>
public interface IDriftApplyOrchestrator
{
    /// <summary>
    /// Runs (or resumes) <paramref name="job"/>. Idempotent: re-entering after a crash never re-runs a
    /// step whose durable result already exists (revalidation's resolved target, or the device-apply
    /// outcome recorded by <see cref="DriftApplyJob.RecordDeviceOutcome"/>), so at most one device write is
    /// ever made per job (AC4/NFR2).
    /// </summary>
    Task RunAsync(DriftApplyJob job, CancellationToken cancellationToken);
}
