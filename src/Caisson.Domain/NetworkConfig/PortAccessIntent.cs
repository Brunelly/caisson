namespace Caisson.Domain.NetworkConfig;

/// <summary>
/// One authored per-port access-VLAN intent within a rack's <see cref="RackNetworkIntent.IntentJson"/>
/// payload (story #168, AC2). Mirrors <see cref="DesiredState.DesiredPortIntent"/>'s "no row = no intent"
/// convention: <see cref="AccessVlanId"/> is <c>null</c> for "Unchanged/Inherit" (no access-VLAN change
/// intended for this port) rather than a separate boolean flag, so reverting a port to
/// Unchanged/Inherit is simply setting this field back to <c>null</c>.
/// </summary>
/// <param name="SwitchStableKey">The discovered switch's stable key (M0 observed-state inventory).</param>
/// <param name="PortName">The discovered port's name on that switch.</param>
/// <param name="AccessVlanId">
/// The intended access VLAN id, or <c>null</c> for Unchanged/Inherit. When set, must reference a VLAN
/// present in the same payload's <c>vlanCatalogue</c> (enforced by <see cref="NetworkIntentValidator"/>).
/// </param>
public sealed record PortAccessIntent(string SwitchStableKey, string PortName, int? AccessVlanId);
