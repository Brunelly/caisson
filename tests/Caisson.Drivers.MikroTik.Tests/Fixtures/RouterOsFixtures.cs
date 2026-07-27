namespace Caisson.Drivers.MikroTik.Tests.Fixtures;

/// <summary>
/// Canned RouterOS <c>!re</c> attribute maps for both a v7-style and a v6-style device, used to prove
/// the mappers absorb firmware-field variance (AC3). v7 rows use <c>true/false</c> booleans and
/// <c>mac-address</c>/<c>interface-name</c> neighbour fields; v6 rows use <c>yes/no</c>, a
/// space-separated VLAN list, a dotted lowercase MAC and a <c>chassis-id</c> neighbour field.
/// </summary>
public static class RouterOsFixtures
{
    public static IReadOnlyDictionary<string, string> Row(params (string Key, string Value)[] pairs)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    public static class V7
    {
        public static IReadOnlyDictionary<string, string> Resource => Row(
            ("version", "7.10.2"), ("board-name", "CCR2004"), ("platform", "MikroTik"));

        public static IReadOnlyDictionary<string, string> Routerboard => Row(
            ("routerboard", "true"), ("model", "CCR2004-1G-12S+2XS"), ("serial-number", "HET081ABCDE"));

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Interfaces => new[]
        {
            Row(("name", "ether1"), ("running", "true"), ("disabled", "false")),
            Row(("name", "ether2"), ("running", "false"), ("disabled", "true")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgePorts => new[]
        {
            Row(("interface", "ether1"), ("pvid", "10")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgeVlans => new[]
        {
            Row(("vlan-ids", "10,20,30-32"), ("tagged", "ether1,ether2"), ("untagged", "ether1")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Neighbours => new[]
        {
            Row(
                ("interface", "ether1"), ("mac-address", "E4:8D:8C:11:22:33"),
                ("identity", "core-sw"), ("interface-name", "sfp-sfpplus1"), ("address", "10.0.0.2")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgeHosts => new[]
        {
            Row(("mac-address", "AA:BB:CC:DD:EE:FF"), ("interface", "ether1")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> VlanInterfaces => new[]
        {
            Row(("name", "vlan10"), ("vlan-id", "10")),
        };
    }

    public static class V6
    {
        public static IReadOnlyDictionary<string, string> Resource => Row(
            ("version", "6.49.7"), ("board-name", "RB750Gr3"), ("platform", "MikroTik"));

        public static IReadOnlyDictionary<string, string> Routerboard => Row(
            ("routerboard", "yes"), ("model", "RB750Gr3"), ("serial-number", "7A1B0ABCDEF"));

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Interfaces => new[]
        {
            Row(("name", "  ether1  "), ("running", "yes"), ("disabled", "no")),
            Row(("name", "ether2"), ("running", "no"), ("disabled", "yes")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgePorts => new[]
        {
            Row(("interface", "ether1"), ("pvid", "10")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgeVlans => new[]
        {
            Row(("vlan-ids", "10 20"), ("tagged", "ether1")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Neighbours => new[]
        {
            Row(("interface", "ether1"), ("chassis-id", "E4:8D:8C:AA:BB:CC"), ("identity", "edge")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> BridgeHosts => new[]
        {
            Row(("mac-address", "aabb.ccdd.eeff"), ("on-interface", "ether1")),
        };

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> VlanInterfaces => new[]
        {
            Row(("name", "vlan20"), ("vlan-id", "20")),
        };
    }

    /// <summary>A Cloud Hosted Router: no RouterBOARD serial.</summary>
    public static class Chr
    {
        public static IReadOnlyDictionary<string, string> Resource => Row(
            ("version", "7.14.2"), ("board-name", "CHR"), ("platform", "MikroTik"));

        public static IReadOnlyDictionary<string, string> Routerboard => Row(("routerboard", "false"));
    }
}
