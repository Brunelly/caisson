namespace Caisson.Domain.Enums;

/// <summary>
/// The kind of drift a <c>DriftItem</c> describes between a rack's desired state and its latest
/// observed topology (story #64, AC1). Persisted as a bounded string; extend append-only as new drift
/// rules are added.
/// </summary>
public enum DriftType
{
    /// <summary>A desired port/switch is absent from the latest observed topology.</summary>
    MissingDesiredEntity = 0,

    /// <summary>An observed port exists on a matched switch but is not declared in desired state.</summary>
    ExtraObservedEntity,

    /// <summary>The desired access VLAN for a port does not match the observed port's Pvid.</summary>
    AccessVlanMismatch,

    /// <summary>The observed port carries tagged/trunk VLANs, which M1 desired-state intent never declares.</summary>
    UnexpectedTrunkConfig,

    /// <summary>A desired LLDP neighbor constraint does not match any observed neighbour on the port.</summary>
    UnexpectedNeighbour,

    /// <summary>A NIC could not be uniquely correlated to a switch port (AC2): non-actionable by construction.</summary>
    UnknownTopologyMapping,
}
