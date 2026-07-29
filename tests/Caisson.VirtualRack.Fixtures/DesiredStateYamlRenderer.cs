namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// Renders <see cref="VirtualRackDefinition"/> — the SAME ground-truth fixture
/// <see cref="RouterOsProfileRenderer"/>/<see cref="RedfishProfileRenderer"/> render into simulator wire
/// formats — into the story #62 git-YAML desired-state shape (<c>desired-state/racks/&lt;rackSlug&gt;.yaml</c>).
/// This is the third renderer of the "one definition, many renderers" pattern: nothing here is derived
/// from the desired-state ingestion pipeline itself, so a regression there shows up as a diff against
/// this independently-authored YAML, not a tautology.
/// </summary>
public static class DesiredStateYamlRenderer
{
    /// <summary>The rack slug this renderer's YAML declares (must match its file name stem, AC1/AC2).</summary>
    public const string RackSlug = "vrack-1";

    /// <summary>
    /// Renders the virtual rack's one switch and four ports (<see cref="VirtualRackDefinition.CleanPort"/>,
    /// <see cref="VirtualRackDefinition.AmbiguousPortA"/>/<see cref="VirtualRackDefinition.AmbiguousPortB"/>,
    /// <see cref="VirtualRackDefinition.UnmappedPort"/>) with their ground-truth access VLANs into the
    /// constrained M1 schema shape.
    /// </summary>
    public static string Render(string? rackSlug = null) => $"""
        rackSlug: {rackSlug ?? RackSlug}
        switches:
          - name: {VirtualRackDefinition.SwitchId}
            ports:
              - name: {VirtualRackDefinition.CleanPort}
                accessVlan: {VirtualRackDefinition.CleanVlan}
                description: clean-port
              - name: {VirtualRackDefinition.AmbiguousPortA}
                accessVlan: {VirtualRackDefinition.AmbiguousVlanA}
              - name: {VirtualRackDefinition.AmbiguousPortB}
                accessVlan: {VirtualRackDefinition.AmbiguousVlanB}
              - name: {VirtualRackDefinition.UnmappedPort}
                accessVlan: {VirtualRackDefinition.UnmappedPortVlan}
        """;

    /// <summary>Renders the same rack with one port's <c>accessVlan</c> pushed out of range (AC2), for negative/partial-accept tests.</summary>
    public static string RenderWithInvalidVlan(string? rackSlug = null) => $"""
        rackSlug: {rackSlug ?? RackSlug}
        switches:
          - name: {VirtualRackDefinition.SwitchId}
            ports:
              - name: {VirtualRackDefinition.CleanPort}
                accessVlan: 5000
        """;

    /// <summary>
    /// Renders the same, otherwise-matching rack (story #64) with <see cref="VirtualRackDefinition.CleanPort"/>'s
    /// desired <c>accessVlan</c> deliberately mismatched against the simulator's actual observed Pvid
    /// (<see cref="VirtualRackDefinition.CleanVlan"/> = 10) — a valid, in-schema value, unlike
    /// <see cref="RenderWithInvalidVlan"/>'s out-of-range 5000, so ingestion succeeds cleanly and the
    /// mismatch surfaces only as drift (<c>DriftType.AccessVlanMismatch</c>), not a validation error. Every
    /// other port's desired VLAN still matches its simulated observed Pvid, so this is the ONLY port-level
    /// drift item the engine should produce for this rack.
    /// </summary>
    public static string RenderWithMismatchedVlan(string? rackSlug = null, int mismatchedVlan = 99) => $"""
        rackSlug: {rackSlug ?? RackSlug}
        switches:
          - name: {VirtualRackDefinition.SwitchId}
            ports:
              - name: {VirtualRackDefinition.CleanPort}
                accessVlan: {mismatchedVlan}
                description: clean-port
              - name: {VirtualRackDefinition.AmbiguousPortA}
                accessVlan: {VirtualRackDefinition.AmbiguousVlanA}
              - name: {VirtualRackDefinition.AmbiguousPortB}
                accessVlan: {VirtualRackDefinition.AmbiguousVlanB}
              - name: {VirtualRackDefinition.UnmappedPort}
                accessVlan: {VirtualRackDefinition.UnmappedPortVlan}
        """;
}
