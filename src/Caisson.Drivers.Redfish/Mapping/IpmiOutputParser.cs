using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Results;

namespace Caisson.Drivers.Redfish.Mapping;

/// <summary>
/// A tolerant, line-oriented parser for the text output of the read-only <c>ipmitool</c> subcommands
/// (<c>mc info</c>, <c>fru print</c>, <c>lan print</c>) — the IPMI analogue of
/// <see cref="Caisson.Drivers.MikroTik.Parsing.RouterOsRecord"/>. Every line is <c>key : value</c>; a
/// malformed line degrades to a diagnostic (or is skipped) rather than throwing, and the resulting
/// <see cref="IpmiRecord"/> maps into the same story-3 Bmc info records the Redfish path produces so the
/// fallback data is indistinguishable downstream except for its provenance diagnostics.
/// </summary>
public static class IpmiOutputParser
{
    /// <summary>Parses <c>key : value</c> lines into a case-insensitive record, degrading bad lines to diagnostics.</summary>
    public static IpmiRecord Parse(string? text, string section, List<DriverDiagnostic> diagnostics)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new IpmiRecord(map);
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                // A non key/value line (e.g. a banner). Note it once as a low-severity diagnostic and move on.
                diagnostics.Add(new DriverDiagnostic(
                    DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, section,
                    "An ipmitool output line was not in 'key : value' form and was skipped."));
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0 && !map.ContainsKey(key))
            {
                // First value wins — fru print repeats keys across FRU devices; the builtin device is first.
                map[key] = value;
            }
        }

        return new IpmiRecord(map);
    }

    /// <summary>
    /// Maps the parsed <c>mc info</c> and <c>fru print</c> records into <see cref="BmcSystemInventory"/>.
    /// IPMI exposes no UUID, so identity keys on the FRU serial; when that is absent a degraded-identity
    /// warning is emitted, mirroring the Redfish path.
    /// </summary>
    public static BmcSystemInventory MapSystemInventory(
        IpmiRecord mcInfo, IpmiRecord fru, string host, List<DriverDiagnostic> diagnostics)
    {
        var serial = fru.GetString("Product Serial", "Board Serial", "Chassis Serial");
        var model = fru.GetString("Product Name", "Board Product");

        if (serial is null)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.Unknown, $"unknown@{host}",
                "Server identity is degraded: IPMI FRU data reported no serial and IPMI exposes no UUID; " +
                "using the endpoint host as the identifier."));
        }

        // IPMI exposes no hostname/UUID, so Model is the FRU product name and identity keys on the serial.
        return new BmcSystemInventory(BmcType.Redfish, host, BmcUuid: null, Hostname: null, model, serial);
    }

    /// <summary>
    /// Maps a parsed <c>lan print</c> record into the BMC's own network interface (its LAN channel MAC).
    /// A missing/unparseable MAC is included with <c>Mac=null</c> plus a per-NIC diagnostic, matching AC3.
    /// </summary>
    public static IReadOnlyList<BmcNetworkInterfaceInfo> MapNetworkInterfaces(
        IpmiRecord lan, List<DriverDiagnostic> diagnostics)
    {
        const string nicId = "ipmi-lan";
        var rawMac = lan.GetString("MAC Address");

        if (rawMac is null)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, nicId,
                "IPMI 'lan print' reported no MAC address; included with a null MAC."));
            return new[] { new BmcNetworkInterfaceInfo(nicId, Mac: null) };
        }

        if (!MacAddressValue.TryParse(rawMac, out var mac))
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Error, ReasonCode.ParseError, nicId,
                "IPMI 'lan print' reported an unparseable MAC address; included with a null MAC."));
            return new[] { new BmcNetworkInterfaceInfo(nicId, Mac: null) };
        }

        return new[] { new BmcNetworkInterfaceInfo(nicId, mac) };
    }

    /// <summary>
    /// Maps IPMI records onto <see cref="BmcBiosInfo"/>. IPMI has no BIOS version, so vendor comes from FRU
    /// and the version stays null with a diagnostic — a deliberately degraded fallback.
    /// </summary>
    public static BmcBiosInfo MapBiosInfo(IpmiRecord mcInfo, IpmiRecord fru, List<DriverDiagnostic> diagnostics)
    {
        var vendor = fru.GetString("Product Manufacturer", "Board Mfg")
            ?? mcInfo.GetString("Manufacturer Name");

        diagnostics.Add(new DriverDiagnostic(
            DriverDiagnosticSeverity.Warning, ReasonCode.ParseError, "Bios",
            "IPMI does not expose a BIOS version; only the vendor could be recovered from FRU data."));

        return new BmcBiosInfo(vendor, Version: null, ReleaseDate: null);
    }
}

/// <summary>
/// A tolerant reader over one parsed <c>ipmitool</c> output block. Every accessor supports multi-key
/// fallback so field-label variance is absorbed, and a missing key returns <c>null</c> rather than throwing.
/// </summary>
public sealed class IpmiRecord
{
    private readonly IReadOnlyDictionary<string, string> _values;

    /// <summary>Wraps a parsed key/value map.</summary>
    public IpmiRecord(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>The underlying raw key/value map, for evidence/diagnostics.</summary>
    public IReadOnlyDictionary<string, string> Raw => _values;

    /// <summary>Returns the first present, non-blank value among <paramref name="keys"/>, trimmed; else <c>null</c>.</summary>
    public string? GetString(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (_values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
