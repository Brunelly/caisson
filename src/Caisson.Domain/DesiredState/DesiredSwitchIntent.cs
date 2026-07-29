using Caisson.Domain.Topology;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// The switch-level node of the typed desired-state tree, owned by a <see cref="DesiredRackIntent"/>
/// (story #62, AC3). Append-only: rows are inserted once per version and never updated (NFR7).
/// </summary>
public sealed class DesiredSwitchIntent : IAppendOnly
{
    private DesiredSwitchIntent()
    {
        // EF Core materialization constructor.
        SwitchName = null!;
        StableKey = null!;
    }

    public DesiredSwitchIntent(Guid id, Guid desiredRackIntentId, string switchName, string stableKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(switchName);
        ArgumentException.ThrowIfNullOrEmpty(stableKey);
        if (!DesiredStateSchema.IsValidDeviceName(switchName))
        {
            throw new ArgumentException($"'{switchName}' is not a valid switch name.", nameof(switchName));
        }

        Id = id;
        DesiredRackIntentId = desiredRackIntentId;
        SwitchName = switchName;
        StableKey = stableKey;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack intent this switch belongs to.</summary>
    public Guid DesiredRackIntentId { get; private set; }

    /// <summary>The switch's identifier within the rack file.</summary>
    public string SwitchName { get; private set; }

    /// <summary>Stable identifier for this switch node in the desired-state tree.</summary>
    public string StableKey { get; private set; }
}
