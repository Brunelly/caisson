using Caisson.Domain.Topology;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// The port-level leaf of the typed desired-state tree, owned by a <see cref="DesiredSwitchIntent"/>
/// (story #62, AC2/AC3). This is the M1-constrained desired-state scope: per-port access VLAN intent
/// plus an optional description and neighbor constraint — no trunk/VXLAN/bonding intent yet. Append-only:
/// rows are inserted once per version and never updated (NFR7).
/// </summary>
/// <remarks>
/// <see cref="AccessVlan"/> and <see cref="Description"/> are guarded here in the constructor AND by a
/// PostgreSQL <c>CHECK</c> constraint added in <c>DesiredPortIntentConfiguration</c> — the same
/// double-enforcement precedent ADR 0004 established for <c>ConfidenceScore</c>, so the invariant holds
/// even against direct SQL writes.
/// </remarks>
public sealed class DesiredPortIntent : IAppendOnly
{
    private DesiredPortIntent()
    {
        // EF Core materialization constructor.
        PortName = null!;
        StableKey = null!;
    }

    public DesiredPortIntent(
        Guid id,
        Guid desiredSwitchIntentId,
        string portName,
        string stableKey,
        int accessVlan,
        string? description = null,
        string? neighborSystemName = null,
        string? neighborPortId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(portName);
        ArgumentException.ThrowIfNullOrEmpty(stableKey);
        if (!DesiredStateSchema.IsValidDeviceName(portName))
        {
            throw new ArgumentException($"'{portName}' is not a valid port name.", nameof(portName));
        }

        if (accessVlan < DesiredStateSchema.MinVlan || accessVlan > DesiredStateSchema.MaxVlan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accessVlan),
                accessVlan,
                $"accessVlan must be between {DesiredStateSchema.MinVlan} and {DesiredStateSchema.MaxVlan}.");
        }

        if (description is { Length: > 0 } && description.Length > DesiredStateSchema.MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"description exceeds the {DesiredStateSchema.MaxDescriptionLength}-character bound.",
                nameof(description));
        }

        if (neighborSystemName is { Length: > 0 } && neighborSystemName.Length > DesiredStateSchema.MaxNeighborFieldLength)
        {
            throw new ArgumentException(
                $"neighborSystemName exceeds the {DesiredStateSchema.MaxNeighborFieldLength}-character bound.",
                nameof(neighborSystemName));
        }

        if (neighborPortId is { Length: > 0 } && neighborPortId.Length > DesiredStateSchema.MaxNeighborFieldLength)
        {
            throw new ArgumentException(
                $"neighborPortId exceeds the {DesiredStateSchema.MaxNeighborFieldLength}-character bound.",
                nameof(neighborPortId));
        }

        Id = id;
        DesiredSwitchIntentId = desiredSwitchIntentId;
        PortName = portName;
        StableKey = stableKey;
        AccessVlan = accessVlan;
        Description = description;
        NeighborSystemName = neighborSystemName;
        NeighborPortId = neighborPortId;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The switch intent this port belongs to.</summary>
    public Guid DesiredSwitchIntentId { get; private set; }

    /// <summary>The port's name.</summary>
    public string PortName { get; private set; }

    /// <summary>
    /// Stable identifier for this port node, computed via
    /// <see cref="Topology.Diffing.StableKeys.ForSwitchPort(string, string)"/> from the owning switch's
    /// stable key and this port's name — the same identity scheme drift/reconciliation (later stories)
    /// will join against observed-state <c>SwitchPort</c> rows with.
    /// </summary>
    public string StableKey { get; private set; }

    /// <summary>Desired 802.1Q access VLAN, bounded to [<see cref="DesiredStateSchema.MinVlan"/>, <see cref="DesiredStateSchema.MaxVlan"/>].</summary>
    public int AccessVlan { get; private set; }

    /// <summary>Optional, length-bounded free-text description.</summary>
    public string? Description { get; private set; }

    /// <summary>Optional expected LLDP neighbor system name constraint.</summary>
    public string? NeighborSystemName { get; private set; }

    /// <summary>Optional expected LLDP neighbor port id constraint.</summary>
    public string? NeighborPortId { get; private set; }
}
