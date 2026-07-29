using Caisson.Domain.DesiredState;
using Caisson.Domain.Topology.Diffing;
using Caisson.Ingestion.Schema;

namespace Caisson.Ingestion.Materializer;

/// <summary>The typed tree materialised for one rack (story #62, AC3): a rack intent plus its switch and port intents.</summary>
public sealed record MaterializedRackIntent(
    DesiredRackIntent Rack,
    IReadOnlyList<DesiredSwitchIntent> Switches,
    IReadOnlyList<DesiredPortIntent> Ports);

/// <summary>
/// Pure function turning a clean (already-validated) <see cref="ValidatedRackDocument"/> into typed
/// <see cref="DesiredRackIntent"/>/<see cref="DesiredSwitchIntent"/>/<see cref="DesiredPortIntent"/>
/// rows with stable identifiers (AC3). Deliberately operates on the validator's already-typed,
/// already-range-checked field values rather than re-walking the raw YAML node tree, so no validation
/// logic (integer parsing, range/length checks) is duplicated between the two.
/// </summary>
public static class DesiredStateMaterializer
{
    /// <summary>
    /// Materialises one rack's typed tree. <paramref name="newId"/> mirrors
    /// <c>DiscoveryJob.SeedSteps</c>'s injected id-seeding pattern, keeping this function pure and
    /// deterministic under test.
    /// </summary>
    public static MaterializedRackIntent Materialize(
        Guid desiredStateVersionId, ValidatedRackDocument document, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newId);

        var rackStableKey = document.RackSlug;
        var rack = new DesiredRackIntent(newId(), desiredStateVersionId, document.RackSlug, rackStableKey);

        var switches = new List<DesiredSwitchIntent>(document.Switches.Count);
        var ports = new List<DesiredPortIntent>();

        foreach (var validatedSwitch in document.Switches)
        {
            var switchStableKey = $"{rackStableKey}|{validatedSwitch.Name}";
            var switchIntent = new DesiredSwitchIntent(newId(), rack.Id, validatedSwitch.Name, switchStableKey);
            switches.Add(switchIntent);

            foreach (var validatedPort in validatedSwitch.Ports)
            {
                var portStableKey = StableKeys.ForSwitchPort(switchStableKey, validatedPort.Name);
                ports.Add(new DesiredPortIntent(
                    newId(),
                    switchIntent.Id,
                    validatedPort.Name,
                    portStableKey,
                    validatedPort.AccessVlan,
                    validatedPort.Description,
                    validatedPort.NeighborSystemName,
                    validatedPort.NeighborPortId));
            }
        }

        return new MaterializedRackIntent(rack, switches, ports);
    }
}
