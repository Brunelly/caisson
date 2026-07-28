using System.Text.Json;
using Caisson.Drivers.Simulators;

namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// Renders <see cref="VirtualRackDefinition"/> into a <see cref="RedfishProfile"/> for
/// <see cref="RedfishSimulator"/> — a <c>paths</c> dictionary mirroring the odata shape of
/// <c>Fixtures/ilo-success.json</c>. Each NIC is rendered in a different textual MAC format (dash, dot,
/// bare) to prove the driver's MAC parsing is separator-agnostic, exactly as the existing fixture does.
/// </summary>
public static class RedfishProfileRenderer
{
    private const string SystemId = "1";
    private const string CleanNicId = "1";
    private const string AmbiguousNicId = "2";
    private const string UnmappedNicId = "3";

    /// <summary>Renders the BMC side of <see cref="VirtualRackDefinition"/> for the given server id.</summary>
    public static RedfishProfile Render(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/redfish/v1"] = Serialize(new Dictionary<string, object?>
            {
                ["@odata.id"] = "/redfish/v1",
                ["Systems"] = Link("/redfish/v1/Systems"),
                ["Managers"] = Link("/redfish/v1/Managers"),
                ["Chassis"] = Link("/redfish/v1/Chassis"),
            }),
            ["/redfish/v1/Systems"] = Serialize(new Dictionary<string, object?>
            {
                ["@odata.id"] = "/redfish/v1/Systems",
                ["Members@odata.count"] = 1,
                ["Members"] = new[] { Link(SystemPath) },
            }),
            [SystemPath] = Serialize(new Dictionary<string, object?>
            {
                ["@odata.id"] = SystemPath,
                ["Id"] = SystemId,
                ["UUID"] = VirtualRackDefinition.ServerUuid,
                ["SerialNumber"] = VirtualRackDefinition.ServerSerial,
                ["Model"] = VirtualRackDefinition.ServerModel,
                ["Manufacturer"] = VirtualRackDefinition.ServerManufacturer,
                ["HostName"] = VirtualRackDefinition.ServerHostName,
                ["BiosVersion"] = VirtualRackDefinition.ServerBiosVersion,
                ["EthernetInterfaces"] = Link(NicsPath),
            }),
            [NicsPath] = Serialize(new Dictionary<string, object?>
            {
                ["@odata.id"] = NicsPath,
                ["Members@odata.count"] = 3,
                ["Members"] = new[] { Link(NicPath(CleanNicId)), Link(NicPath(AmbiguousNicId)), Link(NicPath(UnmappedNicId)) },
            }),
            [NicPath(CleanNicId)] = NicJson(
                CleanNicId, VirtualRackDefinition.CleanNicName, DashFormat(VirtualRackDefinition.CleanNicMac), "LinkUp"),
            [NicPath(AmbiguousNicId)] = NicJson(
                AmbiguousNicId, VirtualRackDefinition.AmbiguousNicName, DotFormat(VirtualRackDefinition.AmbiguousNicMac), "LinkUp"),
            [NicPath(UnmappedNicId)] = NicJson(
                UnmappedNicId, VirtualRackDefinition.UnmappedNicName, BareFormat(VirtualRackDefinition.UnmappedNicMac), "LinkDown"),
        };

        return new RedfishProfile(AuthFail: false, paths);
    }

    /// <summary>Renders an <c>authFail</c> profile (AC3): the simulator answers 401 to every request.</summary>
    public static RedfishProfile RenderAuthFailure()
        => new(AuthFail: true, new Dictionary<string, string>(StringComparer.Ordinal));

    private static string SystemPath => $"/redfish/v1/Systems/{SystemId}";

    private static string NicsPath => $"{SystemPath}/EthernetInterfaces";

    private static string NicPath(string nicId) => $"{NicsPath}/{nicId}";

    private static string NicJson(string id, string name, string mac, string linkStatus) => Serialize(new Dictionary<string, object?>
    {
        ["@odata.id"] = NicPath(id),
        ["Id"] = id,
        ["Name"] = name,
        ["MACAddress"] = mac,
        ["LinkStatus"] = linkStatus,
    });

    private static Dictionary<string, object?> Link(string odataId)
        => new(StringComparer.Ordinal) { ["@odata.id"] = odataId };

    private static string Serialize(object value) => JsonSerializer.Serialize(value);

    private static string DashFormat(string colonMac) => colonMac.Replace(':', '-').ToLowerInvariant();

    private static string BareFormat(string colonMac) => colonMac.Replace(":", string.Empty).ToLowerInvariant();

    /// <summary>Cisco dot-grouped form (e.g. <c>001a.2baa.aa02</c>).</summary>
    private static string DotFormat(string colonMac)
    {
        var hex = BareFormat(colonMac);
        return $"{hex[..4]}.{hex[4..8]}.{hex[8..12]}";
    }
}
