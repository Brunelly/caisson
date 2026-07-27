namespace Caisson.Drivers.Abstractions.Identity;

/// <summary>
/// Identity/capability metadata for a driver implementation. This is the resolution key the
/// registry (see <c>Registry/</c>) uses to find the right factory for a vendor/model/connection kind,
/// and it is exposed as a property (not a method) on every driver instance since it never implies
/// mutation.
/// </summary>
/// <param name="Vendor">The device vendor this driver targets, e.g. <c>"MikroTik"</c>.</param>
/// <param name="Model">The specific model this driver targets, if narrower than the whole vendor line.</param>
/// <param name="ConnectionKind">The transport/protocol this driver uses.</param>
/// <param name="DriverVersion">The driver implementation's own version, for logging/diagnostics.</param>
public sealed record DriverDescriptor(
    string Vendor, string? Model, DriverConnectionKind ConnectionKind, string DriverVersion);
