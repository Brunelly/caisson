namespace Caisson.Domain.Enums;

/// <summary>The management interface type observed for a server's baseboard management controller.</summary>
public enum BmcType
{
    /// <summary>Unknown or undetermined BMC type.</summary>
    Unknown = 0,

    /// <summary>Redfish-capable BMC.</summary>
    Redfish,

    /// <summary>IPMI-capable BMC.</summary>
    Ipmi,
}
