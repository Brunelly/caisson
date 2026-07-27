namespace Caisson.Domain.Enums;

/// <summary>Where an observed MAC address came from.</summary>
public enum MacSource
{
    /// <summary>Observed from BMC / server inventory.</summary>
    Bmc = 0,

    /// <summary>Observed from a switch bridge/forwarding or LLDP table.</summary>
    Switch,
}
