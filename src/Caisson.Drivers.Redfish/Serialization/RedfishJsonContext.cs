using System.Text.Json.Serialization;
using Caisson.Drivers.Redfish.Model;

namespace Caisson.Drivers.Redfish.Serialization;

/// <summary>
/// The source-generated <see cref="JsonSerializerContext"/> over the Redfish DTOs (see
/// <c>Model/RedfishModels.cs</c>). Using <see cref="JsonSerializableAttribute"/> source generation keeps
/// deserialization reflection-free and NativeAOT-safe (the story's NativeAOT constraint / ADR 0009) —
/// there is no runtime-reflection serialization anywhere in the driver. Property-name matching is
/// case-insensitive so iLO's occasional casing drift does not break parsing.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ServiceRoot))]
[JsonSerializable(typeof(RedfishCollection))]
[JsonSerializable(typeof(OdataLink))]
[JsonSerializable(typeof(ComputerSystem))]
[JsonSerializable(typeof(ComputerSystemLinks))]
[JsonSerializable(typeof(EthernetInterface))]
[JsonSerializable(typeof(Bios))]
[JsonSerializable(typeof(Manager))]
public sealed partial class RedfishJsonContext : JsonSerializerContext
{
}
