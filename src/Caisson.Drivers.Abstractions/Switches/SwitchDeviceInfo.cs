namespace Caisson.Drivers.Abstractions.Switches;

/// <summary>
/// Identity/version information observed for a switch. Mirrors the discoverable fields of
/// <c>Caisson.Domain.Topology.Switch</c>, minus persistence identity (no <c>Id</c>/<c>RackId</c>/
/// <c>SnapshotId</c> — those are attached later by the discovery pipeline, not the driver).
/// </summary>
/// <param name="ManagementIp">Observed management IP address, if known.</param>
/// <param name="Serial">Observed serial number, if known.</param>
/// <param name="Model">Observed hardware model, if known.</param>
/// <param name="OsVersion">Observed OS/firmware version, if known.</param>
public sealed record SwitchDeviceInfo(string? ManagementIp, string? Serial, string? Model, string? OsVersion);
