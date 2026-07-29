namespace Caisson.Domain.Enums;

/// <summary>
/// The two fixed steps of a <see cref="Drift.Apply.DriftApplyJob"/> (story #65). Collapsed from the
/// story's illustrative five-step outline (Claimed → Revalidated → AppliedToDevice → Verified/Confirmed
/// → Completed) to two, because the #66 <c>ISwitchMutatingDriver.SetAccessVlanAsync</c> already performs
/// apply + verify + confirm + auto-rollback in a single call — a separate Verify/Confirm job step would
/// have nothing left to do.
/// </summary>
public enum DriftApplyStepName
{
    /// <summary>Re-diffs the target drift item against the latest observed snapshot (AC3).</summary>
    Revalidation = 0,

    /// <summary>Drives the switch-mutating driver's single <c>SetAccessVlanAsync</c> call.</summary>
    DeviceApply,
}
