namespace Caisson.Domain.Enums;

/// <summary>
/// The status of a single step within a <see cref="Drift.Apply.DriftApplyJob"/> (story #65). Mirrors
/// <see cref="DiscoveryStepStatus"/>'s shape so a restarted runner can resume from the last completed
/// step by inspecting each step's durable status.
/// </summary>
public enum DriftApplyStepStatus
{
    /// <summary>The step has not started yet.</summary>
    Pending = 0,

    /// <summary>The step is currently executing.</summary>
    InProgress,

    /// <summary>The step completed successfully.</summary>
    Succeeded,

    /// <summary>The step failed after exhausting its retries.</summary>
    Failed,

    /// <summary>The step was skipped (already succeeded on a prior attempt, or the job reached a terminal state).</summary>
    Skipped,
}
