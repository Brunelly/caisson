namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>The kind of rack entity a <see cref="EntityRef"/> (and thus a <see cref="PreflightIssue"/>) points at.</summary>
public enum EntityKind
{
    /// <summary>The rack as a whole (e.g. a missing-topology error that is not tied to a single VLAN or port).</summary>
    Rack,

    /// <summary>A discovered switch, identified by its stable key.</summary>
    Switch,

    /// <summary>A discovered switch port, identified by its switch's stable key plus the port name.</summary>
    Port,

    /// <summary>An authored VLAN catalogue entry, identified by its VLAN id.</summary>
    Vlan,
}

/// <summary>
/// A stable, machine-readable pointer to the rack entity a <see cref="PreflightIssue"/> concerns
/// (story #170, AC4 "each issue is associated to a deterministic fieldPath and entityRef"). Pure value
/// carrier; the API layer projects it onto <c>EntityRefDto</c> verbatim. Deterministic for a given input
/// so automation can key off it across re-runs.
/// </summary>
/// <param name="Kind">Which entity kind this reference addresses.</param>
/// <param name="RackId">The rack the entity belongs to (always populated).</param>
/// <param name="SwitchStableKey">The switch's stable key, when <see cref="Kind"/> is switch or port.</param>
/// <param name="PortName">The port name, when <see cref="Kind"/> is port.</param>
/// <param name="VlanId">The VLAN id, when <see cref="Kind"/> is vlan.</param>
public sealed record EntityRef(
    EntityKind Kind,
    Guid RackId,
    string? SwitchStableKey = null,
    string? PortName = null,
    int? VlanId = null)
{
    /// <summary>A rack-scoped reference (no VLAN/switch/port narrowing).</summary>
    public static EntityRef Rack(Guid rackId) => new(EntityKind.Rack, rackId);

    /// <summary>A reference to a specific VLAN catalogue entry by id.</summary>
    public static EntityRef Vlan(Guid rackId, int vlanId) => new(EntityKind.Vlan, rackId, VlanId: vlanId);

    /// <summary>A reference to a specific switch by stable key.</summary>
    public static EntityRef Switch(Guid rackId, string switchStableKey)
        => new(EntityKind.Switch, rackId, switchStableKey);

    /// <summary>A reference to a specific switch port by switch stable key + port name.</summary>
    public static EntityRef Port(Guid rackId, string switchStableKey, string portName)
        => new(EntityKind.Port, rackId, switchStableKey, portName);
}
