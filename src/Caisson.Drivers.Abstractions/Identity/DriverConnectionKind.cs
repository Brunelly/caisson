namespace Caisson.Drivers.Abstractions.Identity;

/// <summary>The transport/protocol a driver uses to connect to a device.</summary>
public enum DriverConnectionKind
{
    /// <summary>Unknown or undetermined connection kind.</summary>
    Unknown = 0,

    /// <summary>MikroTik RouterOS API.</summary>
    RouterOsApi,

    /// <summary>SSH-based connection.</summary>
    Ssh,

    /// <summary>Redfish (DMTF) over HTTPS.</summary>
    Redfish,

    /// <summary>IPMI.</summary>
    Ipmi,
}
