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

    /// <summary>The job was canceled by an operator/admin.</summary>
    public const string Canceled = "CANCELED";

    /// <summary>An unexpected error aborted the step/job.</summary>
    public const string UnexpectedError = "UNEXPECTED_ERROR";
}
