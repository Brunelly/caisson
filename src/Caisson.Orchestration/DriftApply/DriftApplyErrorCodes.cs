namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// Stable, operator-safe error codes surfaced on failed/stale-drift jobs and steps (NFR4). Deliberately
/// coarse and carry no device-specific or secret detail. Terminal outcomes from the device-apply step use
/// <c>Caisson.Drivers.Abstractions.Mutating.SwitchChangeReasonCode</c> names directly instead — this class
/// only covers failures the apply orchestration itself detects (before or around the driver call).
/// </summary>
public static class DriftApplyErrorCodes
{
    /// <summary>No config-bound discovery definition exists for the rack (fail-closed).</summary>
    public const string RackDefinitionMissing = "RACK_DEFINITION_MISSING";

    /// <summary>Revalidation found the target drift item no longer present in the latest recomputed report (AC3).</summary>
    public const string DriftItemGone = "DRIFT_ITEM_GONE";

    /// <summary>Revalidation found the item's expected/observed values no longer match the anchors captured at request time (AC3).</summary>
    public const string DriftAnchorsMismatched = "DRIFT_ANCHORS_MISMATCHED";

    /// <summary>The resolved switch device key does not match any switch in the rack's discovery definition.</summary>
    public const string SwitchNotConfigured = "SWITCH_NOT_CONFIGURED";

    /// <summary>No write-capable driver is registered for the target switch's vendor/model/connection-kind descriptor.</summary>
    public const string DriverNotFound = "DRIVER_NOT_FOUND";

    /// <summary>The device-mutating driver call failed for infrastructure reasons (connect/auth/timeout), with no device state change.</summary>
    public const string DeviceCallFailed = "DEVICE_CALL_FAILED";

    /// <summary>
    /// The job was excluded from reclaim because it reached <c>MaxJobAttempts</c> — reclaimed and crashed
    /// too many times to keep retrying.
    /// </summary>
    public const string MaxAttemptsExceeded = "MAX_ATTEMPTS_EXCEEDED";

    /// <summary>An unexpected error aborted the step/job.</summary>
    public const string UnexpectedError = "UNEXPECTED_ERROR";

    /// <summary>
    /// Maps a stable error code to its fixed, operator-safe message. Failure paths that would otherwise
    /// surface a raw exception message (which can leak internal SQL/host/constraint detail through the
    /// read-accessible job-status endpoint — OWASP A05) use this instead; the full exception is logged
    /// server-side only, keyed off the correlation id.
    /// </summary>
    public static string MessageFor(string errorCode) => errorCode switch
    {
        RackDefinitionMissing => "No discovery definition is configured for the rack.",
        DriftItemGone => "The drift item is no longer present in the latest recomputed drift report.",
        DriftAnchorsMismatched => "The drift item's expected/observed values have changed since the apply was requested.",
        SwitchNotConfigured => "The target switch is not configured in the rack's discovery definition.",
        DriverNotFound => "No write-capable driver is registered for the target switch.",
        DeviceCallFailed => "The device call failed; no device state was changed.",
        MaxAttemptsExceeded => "The job was reclaimed and failed too many times and will not be retried further.",
        _ => "An unexpected error occurred while applying the drift correction.",
    };
}

/// <summary>Coarse classification stored in <c>DriftApplyJob.ErrorCategory</c> (story #65, AC6).</summary>
public static class DriftApplyErrorCategories
{
    /// <summary>The request itself was invalid (should normally be rejected before a job is even created).</summary>
    public const string Validation = "Validation";

    /// <summary>Revalidation found the drift no longer current (AC3).</summary>
    public const string StaleDrift = "StaleDrift";

    /// <summary>The device rejected or could not confirm the change (a <c>SwitchChangeReasonCode</c> terminal outcome).</summary>
    public const string DeviceRejected = "DeviceRejected";

    /// <summary>An infrastructure failure (DB, driver connectivity, missing configuration) aborted the job.</summary>
    public const string Infrastructure = "Infrastructure";
}
