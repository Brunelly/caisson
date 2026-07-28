namespace Caisson.Domain.Enums;

/// <summary>
/// The observed-entity kinds that carry a stable natural key and are therefore diffed between
/// consecutive snapshots (see <c>StableKeys</c> and <c>TopologyEntityDiff</c>). Persisted as a bounded
/// string; extended append-only like the other Caisson enums.
/// </summary>
public enum TopologyEntityType
{
    /// <summary>An observed switch.</summary>
    Switch = 0,

    /// <summary>An observed switch port.</summary>
    SwitchPort,

    /// <summary>An observed server.</summary>
    Server,

    /// <summary>An observed NIC.</summary>
    Nic,

    /// <summary>An observed MAC address.</summary>
    Mac,

    /// <summary>An observed VLAN.</summary>
    Vlan,

    /// <summary>An observed LLDP neighbour.</summary>
    Lldp,
}
