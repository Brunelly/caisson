using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Orchestration.RackDefinitions;

/// <summary>
/// The config-bound, secret-free rack definition (story #8, ADR 0013). It is the desired-state input the
/// orchestrator reads to know <b>what</b> to discover: for each rack (keyed by <see cref="Rack.ExternalKey"/>)
/// the switches and servers with their connection options and an <b>opaque</b> credentials reference.
/// It deliberately contains no secret material — drivers resolve the actual credential from the secret
/// store using <see cref="DeviceDefinitionEntry.CredentialsRef"/>.
/// </summary>
public sealed class RackDefinitionOptions
{
    /// <summary>Configuration section this binds from (the <c>Racks</c> list lives under it).</summary>
    public const string SectionName = "Discovery";

    /// <summary>The defined racks, each keyed by its stable external key.</summary>
    public List<RackDefinitionEntry> Racks { get; set; } = new();
}

/// <summary>One rack's device inventory in configuration.</summary>
public sealed class RackDefinitionEntry
{
    /// <summary>The stable external key matching a <c>Rack.ExternalKey</c> in the registry.</summary>
    public string ExternalKey { get; set; } = string.Empty;

    /// <summary>The switches to discover in this rack.</summary>
    public List<DeviceDefinitionEntry> Switches { get; set; } = new();

    /// <summary>The servers (BMCs) to discover in this rack.</summary>
    public List<DeviceDefinitionEntry> Servers { get; set; } = new();
}

/// <summary>
/// One device's connection definition. Mirrors <see cref="SwitchConnectionOptions"/>/
/// <see cref="BmcConnectionOptions"/> field-for-field but carries only an opaque
/// <see cref="CredentialsRef"/>, never a secret.
/// </summary>
public sealed class DeviceDefinitionEntry
{
    /// <summary>Caller-stable device identifier used as the correlation <c>SwitchId</c>/<c>ServerId</c>.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>Driver vendor selector (e.g. <c>MikroTik</c>, <c>HPE</c>).</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>Optional driver model selector.</summary>
    public string? Model { get; set; }

    /// <summary>The connection kind used to resolve the driver.</summary>
    public DriverConnectionKind ConnectionKind { get; set; }

    /// <summary>Device host/address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Optional device port (driver default when null).</summary>
    public int? Port { get; set; }

    /// <summary>Per-device driver call timeout, in seconds (0 → use the orchestration default).</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Opaque reference the driver resolves against the secret store — never a secret.</summary>
    public string CredentialsRef { get; set; } = string.Empty;
}
