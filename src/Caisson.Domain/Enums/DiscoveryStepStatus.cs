namespace Caisson.Domain.Enums;

/// <summary>
/// The status of a single step within a discovery job (story #8, AC1). Persisted as a bounded string so
/// a restarted runner can resume from the last completed step by inspecting each step's durable status.
/// </summary>
public enum DiscoveryStepStatus
{
    /// <summary>The step has not started yet.</summary>
    Pending = 0,

    /// <summary>The step is currently executing.</summary>
    InProgress,

    /// <summary>The step completed successfully.</summary>
    Succeeded,

    /// <summary>The step failed after exhausting its retries.</summary>
    Failed,

    /// <summary>The step was skipped (already succeeded on a prior attempt, or the job was canceled).</summary>
    Skipped,
}
