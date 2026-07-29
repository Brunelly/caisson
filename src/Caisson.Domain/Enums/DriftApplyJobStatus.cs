namespace Caisson.Domain.Enums;

/// <summary>
/// The durable lifecycle of a <see cref="Drift.Apply.DriftApplyJob"/> (story #65, AC4). Mirrors
/// <see cref="DiscoveryJobStatus"/>'s shape: non-terminal states drive the runner's claim/reclaim
/// predicate, terminal states never transition again.
/// </summary>
public enum DriftApplyJobStatus
{
    /// <summary>Queued, not yet claimed by a runner instance.</summary>
    Pending = 0,

    /// <summary>Claimed by a runner instance; about to (re)start revalidation.</summary>
    Claimed,

    /// <summary>Re-diffing the target drift item against the latest observed snapshot.</summary>
    Revalidating,

    /// <summary>Driving the switch-mutating driver's <c>SetAccessVlanAsync</c>.</summary>
    Executing,

    /// <summary>Terminal: the device change was applied (or already matched the desired state).</summary>
    Completed,

    /// <summary>Terminal: validation, revalidation infrastructure, or the device change failed.</summary>
    Failed,

    /// <summary>Terminal: revalidation found the drift item gone or no longer matching its anchors (AC3).</summary>
    StaleDrift,

    /// <summary>Terminal: the job was canceled before it reached a device-mutating step.</summary>
    Canceled,
}
