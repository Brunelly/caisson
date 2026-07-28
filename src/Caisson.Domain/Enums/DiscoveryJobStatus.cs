namespace Caisson.Domain.Enums;

/// <summary>
/// The lifecycle state of a discovery orchestration job (story #8). A job progresses
/// <see cref="Queued"/> → <see cref="InProgress"/> → one terminal state
/// (<see cref="Succeeded"/>/<see cref="Failed"/>/<see cref="Canceled"/>). Persisted as a bounded
/// string so transitions are durable across process restarts (AC1).
/// </summary>
public enum DiscoveryJobStatus
{
    /// <summary>The job has been created and is waiting to be claimed by the runner.</summary>
    Queued = 0,

    /// <summary>The runner has claimed the job and is executing its steps.</summary>
    InProgress,

    /// <summary>Every step completed (or was skipped) and the snapshot was persisted.</summary>
    Succeeded,

    /// <summary>The job stopped on an unrecoverable error; the failure is recorded.</summary>
    Failed,

    /// <summary>The job was canceled by an operator/admin before completing.</summary>
    Canceled,
}
