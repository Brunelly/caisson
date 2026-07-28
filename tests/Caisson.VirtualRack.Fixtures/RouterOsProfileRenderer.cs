using Caisson.Drivers.Simulators;

namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// Renders <see cref="VirtualRackDefinition"/> into a <see cref="RouterOsProfile"/> for
/// <see cref="RouterOsApiSimulator"/> — populating <c>/interface/print</c>,
/// <c>/interface/bridge/port/print</c>, <c>/interface/bridge/vlan/print</c>,
/// <c>/interface/bridge/host/print</c> and, critically, <c>/ip/neighbor/print</c> LLDP rows from the same
/// ground truth (mirroring the row shapes of <c>Fixtures/v7.json</c>).
/// </summary>
public static class RouterOsProfileRenderer
{
    /// <summary>Renders the switch side of <see cref="VirtualRackDefinition"/>.</summary>
    public static RouterOsProfile Render()
    {
        var profile = new RouterOsProfile { LegacyLogin = false };

        profile.Commands["/system/resource/print"] = Reply(
            Row(
                ("version", VirtualRackDefinition.SwitchOsVersion),
                ("board-name", VirtualRackDefinition.SwitchBoardName),
                ("platform", VirtualRackDefinition.SwitchPlatform)));

        // No rows: CHR has no RouterBOARD serial, which is exactly what makes MapDeviceInfo leave Serial
        // null and the switch's stable key fall back to the (deterministic, always-loopback) management IP.
        profile.Commands["/system/routerboard/print"] = Reply();

        profile.Commands["/interface/print"] = Reply(
            InterfaceRow(VirtualRackDefinition.CleanPort),
            InterfaceRow(VirtualRackDefinition.AmbiguousPortA),
            InterfaceRow(VirtualRackDefinition.AmbiguousPortB),
            InterfaceRow(VirtualRackDefinition.UnmappedPort));

        profile.Commands["/interface/ethernet/print"] = Reply(
            Row(("name", VirtualRackDefinition.CleanPort)),
            Row(("name", VirtualRackDefinition.AmbiguousPortA)),
            Row(("name", VirtualRackDefinition.AmbiguousPortB)),
            Row(("name", VirtualRackDefinition.UnmappedPort)));

        profile.Commands["/interface/bridge/port/print"] = Reply(
            PvidRow(VirtualRackDefinition.CleanPort, VirtualRackDefinition.CleanVlan),
            PvidRow(VirtualRackDefinition.AmbiguousPortA, VirtualRackDefinition.AmbiguousVlanA),
            PvidRow(VirtualRackDefinition.AmbiguousPortB, VirtualRackDefinition.AmbiguousVlanB),
            PvidRow(VirtualRackDefinition.UnmappedPort, VirtualRackDefinition.UnmappedPortVlan));

        // Registers each VLAN in the switch-wide table without tagging any port onto it — every port here
        // is access-only (a single untagged/PVID VLAN), never a trunk (ADR 0010 / CorrelationScoring).
        profile.Commands["/interface/bridge/vlan/print"] = Reply(
            Row(("vlan-ids", VirtualRackDefinition.CleanVlan.ToString())),
            Row(("vlan-ids", VirtualRackDefinition.AmbiguousVlanA.ToString())),
            Row(("vlan-ids", VirtualRackDefinition.AmbiguousVlanB.ToString())),
            Row(("vlan-ids", VirtualRackDefinition.UnmappedPortVlan.ToString())));

        profile.Commands["/interface/vlan/print"] = Reply();

        // Only the clean NIC's port carries an LLDP neighbour row — this is the fidelity guard: a genuine
        // in-process wire round trip populates it from the same ground truth, so the happy-path assertion
        // can require LldpConsistent (not just MacLearnUnique) and a regression to MAC-only fails.
        profile.Commands["/ip/neighbor/print"] = Reply(
            Row(
                ("interface", VirtualRackDefinition.CleanPort),
                ("mac-address", VirtualRackDefinition.LldpChassisId),
                ("identity", VirtualRackDefinition.LldpSystemName),
                ("interface-name", VirtualRackDefinition.LldpPortId),
                ("address", VirtualRackDefinition.LldpMgmtAddress)));

        profile.Commands["/interface/bridge/host/print"] = Reply(
            Row(("mac-address", VirtualRackDefinition.CleanNicMac), ("on-interface", VirtualRackDefinition.CleanPort)),
            Row(("mac-address", VirtualRackDefinition.AmbiguousNicMac), ("on-interface", VirtualRackDefinition.AmbiguousPortA)),
            Row(("mac-address", VirtualRackDefinition.AmbiguousNicMac), ("on-interface", VirtualRackDefinition.AmbiguousPortB)),
            Row(("mac-address", VirtualRackDefinition.ForeignMac), ("on-interface", VirtualRackDefinition.UnmappedPort)));

        return profile;
    }

    private static Dictionary<string, string> InterfaceRow(string name)
        => Row(("name", name), ("running", "true"), ("disabled", "false"));

    private static Dictionary<string, string> PvidRow(string port, int vlan)
        => Row(("interface", port), ("pvid", vlan.ToString()));

    private static RouterOsCommandReply Reply(params Dictionary<string, string>[] rows)
        => new() { Rows = rows.ToList() };

    private static Dictionary<string, string> Row(params (string Key, string Value)[] pairs)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            row[key] = value;
        }

        return row;
    }
}
