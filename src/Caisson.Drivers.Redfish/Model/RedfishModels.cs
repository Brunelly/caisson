using System.Text.Json.Serialization;

namespace Caisson.Drivers.Redfish.Model;

/// <summary>
/// Minimal Redfish DTO records covering exactly the fields the read-only discovery driver consumes. Every
/// property is nullable and annotated with its wire name via <see cref="JsonPropertyNameAttribute"/> so
/// iLO casing/absence variance is tolerated rather than fatal (the mappers degrade a missing field to a
/// diagnostic — AC3). These are (de)serialized only through the source-generated
/// <see cref="Caisson.Drivers.Redfish.Serialization.RedfishJsonContext"/>, keeping the driver
/// reflection-free and AOT-compatible.
/// </summary>
/// <param name="Id">The Redfish resource id — <c>@odata.id</c>, the canonical resource path.</param>
public sealed record OdataLink(
    [property: JsonPropertyName("@odata.id")] string? Id);

/// <summary>The Redfish service root (<c>/redfish/v1</c>): the entry point with links to the collections.</summary>
public sealed record ServiceRoot(
    [property: JsonPropertyName("Systems")] OdataLink? Systems,
    [property: JsonPropertyName("Managers")] OdataLink? Managers,
    [property: JsonPropertyName("Chassis")] OdataLink? Chassis);

/// <summary>A Redfish collection resource: a list of member links plus an optional count.</summary>
public sealed record RedfishCollection(
    [property: JsonPropertyName("Members")] IReadOnlyList<OdataLink>? Members,
    [property: JsonPropertyName("Members@odata.count")] int? Count);

/// <summary>Nested <c>Links</c> on a <c>ComputerSystem</c> (managers/chassis back-references).</summary>
public sealed record ComputerSystemLinks(
    [property: JsonPropertyName("ManagedBy")] IReadOnlyList<OdataLink>? ManagedBy,
    [property: JsonPropertyName("Chassis")] IReadOnlyList<OdataLink>? Chassis);

/// <summary>A Redfish <c>ComputerSystem</c> — the server identity/inventory resource.</summary>
public sealed record ComputerSystem(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("UUID")] string? Uuid,
    [property: JsonPropertyName("SerialNumber")] string? SerialNumber,
    [property: JsonPropertyName("Model")] string? Model,
    [property: JsonPropertyName("Manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("HostName")] string? HostName,
    [property: JsonPropertyName("BiosVersion")] string? BiosVersion,
    [property: JsonPropertyName("EthernetInterfaces")] OdataLink? EthernetInterfaces,
    [property: JsonPropertyName("Bios")] OdataLink? Bios,
    [property: JsonPropertyName("Links")] ComputerSystemLinks? Links);

/// <summary>A Redfish <c>EthernetInterface</c> — a single NIC with its MAC address(es) and link status.</summary>
public sealed record EthernetInterface(
    [property: JsonPropertyName("@odata.id")] string? OdataId,
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("MACAddress")] string? MacAddress,
    [property: JsonPropertyName("PermanentMACAddress")] string? PermanentMacAddress,
    [property: JsonPropertyName("LinkStatus")] string? LinkStatus);

/// <summary>A Redfish <c>Bios</c> resource — carries the vendor/version attribute bag where present.</summary>
public sealed record Bios(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("AttributeRegistry")] string? AttributeRegistry);

/// <summary>A Redfish <c>Manager</c> — the BMC/iLO itself (firmware/identity).</summary>
public sealed record Manager(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Model")] string? Model,
    [property: JsonPropertyName("Manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("FirmwareVersion")] string? FirmwareVersion);
