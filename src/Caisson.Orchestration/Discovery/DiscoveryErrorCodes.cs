namespace Caisson.Orchestration.Discovery;

/// <summary>
/// Stable, operator-safe error codes surfaced on failed jobs/steps (NFR4). They are intentionally
/// coarse and carry no device-specific or secret detail.
/// </summary>
public static class DiscoveryErrorCodes
{
    /// <summary>No config-bound definition exists for the rack (fail-closed, AC/ADR 0013).</summary>
    public const string RackDefinitionMissing = "RACK_DEFINITION_MISSING";

    /// <summary>No driver is registered for a device's vendor/model/connection-kind descriptor.</summary>
    public const string DriverNotFound = "DRIVER_NOT_FOUND";

    /// <summary>Every switch in the rack failed discovery.</summary>
    public const string SwitchDiscoveryFailed = "SWITCH_DISCOVERY_FAILED";

    /// <summary>Every server/BMC in the rack failed discovery.</summary>
    public const string BmcDiscoveryFailed = "BMC_DISCOVERY_FAILED";

    /// <summary>Persisting the correlated snapshot failed.</summary>
    public const string PersistenceFailed = "PERSISTENCE_FAILED";

    /// <summary>An unexpected error aborted the step/job.</summary>
    public const string UnexpectedError = "UNEXPECTED_ERROR";

    /// <summary>
    /// Maps a stable error code to its fixed, operator-safe message. Failure paths that would otherwise
    /// surface a raw exception message (which can leak internal SQL/host/constraint detail through the
    /// read-accessible job-detail endpoint — OWASP A05) use this instead; the full exception is logged
    /// server-side only, keyed off the correlation id.
    /// </summary>
    public static string MessageFor(string errorCode) => errorCode switch
    {
        RackDefinitionMissing => "No discovery definition is configured for the rack.",
        DriverNotFound => "No driver is registered for a device in the rack.",
        SwitchDiscoveryFailed => "All switches failed discovery.",
        BmcDiscoveryFailed => "All servers failed discovery.",
        PersistenceFailed => "Persisting the discovery snapshot failed.",
        _ => "An unexpected error occurred during discovery.",
    };
}
