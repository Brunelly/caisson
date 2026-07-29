namespace Caisson.Drivers.Abstractions.Mutating;

/// <summary>
/// A base type for one typed, human-describable step of a <see cref="SwitchChangePlan"/>. Every step is
/// a typed record — never a raw RouterOS (or other vendor) command string — so a dry-run plan or an
/// audit record can be inspected/logged/rendered without ever exposing a command-injection surface
/// (NFR1).
/// </summary>
public abstract record SwitchChangeStep
{
    /// <summary>A human-readable description of this step, suitable for a dry-run preview or audit UI.</summary>
    public abstract string Description { get; }
}

/// <summary>A planned or applied change to a bridge port's PVID (access VLAN).</summary>
/// <param name="PortName">The port whose PVID is changing.</param>
/// <param name="FromVlanId">The PVID observed before the change.</param>
/// <param name="ToVlanId">The desired PVID after the change.</param>
public sealed record BridgePortPvidChange(string PortName, int FromVlanId, int ToVlanId) : SwitchChangeStep
{
    /// <inheritdoc />
    public override string Description =>
        $"Set port '{PortName}' access VLAN (PVID) from {FromVlanId} to {ToVlanId}.";
}

/// <summary>The confirmed-commit safety step: a self-reverting window armed before the change is applied.</summary>
/// <param name="Window">How long the device will wait for confirmation before reverting automatically.</param>
public sealed record ConfirmedCommitWindowArmed(TimeSpan Window) : SwitchChangeStep
{
    /// <inheritdoc />
    public override string Description =>
        $"Arm a self-reverting rollback that fires automatically if the change is not confirmed within {Window.TotalSeconds:0}s.";
}

/// <summary>An ordered set of typed steps describing an intended (dry-run) or applied switch change.</summary>
/// <param name="Steps">The steps, in the order they were (or would be) executed.</param>
public sealed record SwitchChangePlan(IReadOnlyList<SwitchChangeStep> Steps);
