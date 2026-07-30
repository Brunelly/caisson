namespace Caisson.Domain.NetworkConfig;

/// <summary>
/// One authored VLAN catalogue entry within a rack's <see cref="RackNetworkIntent.IntentJson"/> payload
/// (story #168, AC1). A plain value carrier — validated by <see cref="NetworkIntentValidator"/>, never by
/// its own constructor, so an in-progress (not-yet-valid) authoring draft can still round-trip through
/// the API for the client to show inline errors against.
/// </summary>
/// <param name="Id">The VLAN id (expected 1-4094; see <see cref="DesiredState.DesiredStateSchema"/>).</param>
/// <param name="Name">The VLAN's human-readable name (e.g. "storage").</param>
/// <param name="Description">Optional free-text description (e.g. "iSCSI").</param>
public sealed record VlanCatalogueEntry(int Id, string Name, string? Description);
