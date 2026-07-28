using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Orchestration.RackDefinitions;

/// <summary>
/// The resolved desired-state definition for one rack: its stable id/key and the devices to discover.
/// Produced by <see cref="IRackDefinitionProvider"/> by joining the config entry to the persisted
/// <c>Rack</c>. Secret-free by construction (each device carries only an opaque credentials ref).
/// </summary>
/// <param name="RackId">The stable rack id from the registry.</param>
/// <param name="ExternalKey">The rack's stable external key.</param>
/// <param name="Switches">The switches to discover.</param>
/// <param name="Servers">The servers to discover.</param>
public sealed record RackDefinition(
    Guid RackId,
    string ExternalKey,
    IReadOnlyList<DeviceDefinition> Switches,
    IReadOnlyList<DeviceDefinition> Servers);

/// <summary>
/// One resolved device connection definition. The opaque <see cref="CredentialsRef"/> is the only
/// credential-related field; the driver resolves the real secret from the secret store.
/// </summary>
/// <param name="DeviceKey">Caller-stable device id used as the correlation switch/server id.</param>
/// <param name="Vendor">Driver vendor selector.</param>
/// <param name="Model">Optional driver model selector.</param>
/// <param name="ConnectionKind">Connection kind used to resolve the driver.</param>
/// <param name="Host">Device host/address.</param>
/// <param name="Port">Optional device port.</param>
/// <param name="Timeout">Per-device driver call timeout.</param>
/// <param name="CredentialsRef">Opaque secret-store reference — never a secret.</param>
public sealed record DeviceDefinition(
    string DeviceKey,
    string Vendor,
    string? Model,
    DriverConnectionKind ConnectionKind,
    string Host,
    int? Port,
    TimeSpan Timeout,
    string CredentialsRef)
{
    /// <summary>The descriptor used to resolve this device's driver from the registry.</summary>
    public DriverDescriptor ToDescriptor()
        => new(Vendor, Model, ConnectionKind, DriverVersion: "*");
}
