namespace Caisson.Domain.Enums;

/// <summary>The observed link state of a NIC.</summary>
public enum LinkState
{
    /// <summary>Link state could not be determined.</summary>
    Unknown = 0,

    /// <summary>The link is up.</summary>
    Up,

    /// <summary>The link is down.</summary>
    Down,
}
