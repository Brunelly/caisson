using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Model;

namespace Caisson.Drivers.Redfish.Mapping;

/// <summary>
/// Maps Redfish DTOs (and, via <see cref="IpmiOutputParser"/>, IPMI records) into the story-3 Bmc info
/// records. Each mapper is tolerant of iLO field variance and missing data (AC3), modelled on
/// <see cref="Caisson.Drivers.MikroTik.Mapping.RouterOsMappers"/>: a missing or unparseable field becomes
/// a <see cref="DriverDiagnostic"/> appended to <paramref name="diagnostics"/> rather than an exception, so
/// partial data still yields a usable result.
/// </summary>
public static class RedfishMappers
{
    /// <summary>
    /// Maps a Redfish <see cref="ComputerSystem"/> into <see cref="BmcSystemInventory"/>. Server identity
    /// follows the order UUID → SerialNumber → composite (resource <c>Id</c> + endpoint host); when both
    /// UUID and SerialNumber are absent a degraded-identity <see cref="DriverDiagnosticSeverity.Warning"/>
    /// is emitted (story #5 answered question) — the composite still lets downstream correlation proceed.
    /// </summary>
    public static BmcSystemInventory MapSystemInventory(
        ComputerSystem? system, string host, List<DriverDiagnostic> diagnostics)
    {
        var uuid = Clean(system?.Uuid);
        var serial = Clean(system?.SerialNumber);
        var hostname = Clean(system?.HostName);
        var model = Clean(system?.Model);
        var resourceId = Clean(system?.Id);

        if (uuid is null && serial is null)
        {
            // Identity is degraded: fall back to a stable composite of the Redfish resource id and the
            // endpoint host so correlation can still key on something, and record the caveat (AC1/answered Q).
            var composite = $"{resourceId ?? "unknown"}@{host}";
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.Unknown, composite,
                "Server identity is degraded: neither UUID nor SerialNumber was reported; " +
                "using the Redfish resource id and endpoint host as a composite identifier."));
        }

        return new BmcSystemInventory(BmcType.Redfish, host, uuid, hostname, model, serial);
    }

    /// <summary>
    /// Maps Redfish <see cref="EthernetInterface"/> resources into <see cref="BmcNetworkInterfaceInfo"/>.
    /// The NIC id is normalized from the resource <c>Id</c> (or the trailing <c>@odata.id</c> segment); the
    /// MAC is normalized through <see cref="MacAddressValue.TryParse"/>. A NIC whose MAC is missing or
    /// unparseable is still included with <c>Mac=null</c> and a per-NIC diagnostic naming it (story #5
    /// answered question), so the gap stays visible for correlation debugging (AC3).
    /// </summary>
    public static IReadOnlyList<BmcNetworkInterfaceInfo> MapNetworkInterfaces(
        IReadOnlyList<EthernetInterface?> interfaces, List<DriverDiagnostic> diagnostics)
    {
        var results = new List<BmcNetworkInterfaceInfo>(interfaces.Count);
        for (var i = 0; i < interfaces.Count; i++)
        {
            var nic = interfaces[i];
            if (nic is null)
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, $"interface[{i}]",
                    "An EthernetInterface resource could not be read and was skipped."));
                continue;
            }

            var id = NormalizeNicId(nic) ?? $"interface[{i}]";
            var name = Clean(nic.Name) ?? id;
            var rawMac = Clean(nic.MacAddress) ?? Clean(nic.PermanentMacAddress);
            var linkState = MapLinkState(nic.LinkStatus);

            if (rawMac is null)
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, id,
                    $"Network interface '{id}' reported no MAC address; included with a null MAC."));
                results.Add(new BmcNetworkInterfaceInfo(name, Mac: null, linkState));
                continue;
            }

            if (!MacAddressValue.TryParse(rawMac, out var mac))
            {
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Error, ReasonCode.ParseError, id,
                    $"Network interface '{id}' reported an unparseable MAC address; included with a null MAC."));
                results.Add(new BmcNetworkInterfaceInfo(name, Mac: null, linkState));
                continue;
            }

            results.Add(new BmcNetworkInterfaceInfo(name, mac, linkState));
        }

        return results;
    }

    /// <summary>
    /// Maps BIOS/firmware information onto <see cref="BmcBiosInfo"/>. Redfish carries the BIOS version on the
    /// <see cref="ComputerSystem"/> (<c>BiosVersion</c>) and the vendor via <c>Manufacturer</c>; a missing
    /// version degrades to a diagnostic rather than an error.
    /// </summary>
    public static BmcBiosInfo MapBiosInfo(ComputerSystem? system, List<DriverDiagnostic> diagnostics)
    {
        var vendor = Clean(system?.Manufacturer);
        var version = Clean(system?.BiosVersion);

        if (version is null)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, "Bios",
                "No BIOS version was reported by the Redfish endpoint."));
        }

        return new BmcBiosInfo(vendor, version, ReleaseDate: null);
    }

    /// <summary>Extracts the collection member link paths from a <see cref="RedfishCollection"/>, tolerating nulls.</summary>
    public static IReadOnlyList<string> MemberLinks(RedfishCollection? collection)
    {
        if (collection?.Members is null)
        {
            return Array.Empty<string>();
        }

        var links = new List<string>(collection.Members.Count);
        foreach (var member in collection.Members)
        {
            var link = Clean(member?.Id);
            if (link is not null)
            {
                links.Add(link);
            }
        }

        return links;
    }

    /// <summary>Maps a Redfish <c>LinkStatus</c> string onto the domain <see cref="LinkState"/>.</summary>
    internal static LinkState? MapLinkState(string? linkStatus)
    {
        var value = Clean(linkStatus);
        return value?.ToLowerInvariant() switch
        {
            "linkup" or "up" => LinkState.Up,
            "linkdown" or "nolink" or "down" => LinkState.Down,
            _ => null,
        };
    }

    /// <summary>Prefers the resource <c>Id</c>; else the trailing segment of the <c>@odata.id</c> path.</summary>
    private static string? NormalizeNicId(EthernetInterface nic)
    {
        var id = Clean(nic.Id);
        if (id is not null)
        {
            return id;
        }

        return TrailingSegment(nic.OdataId);
    }

    /// <summary>Returns the last non-empty path segment of <paramref name="path"/>, or <c>null</c>.</summary>
    private static string? TrailingSegment(string? path)
    {
        var value = Clean(path);
        if (value is null)
        {
            return null;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
