using Caisson.Domain.Enums;

namespace Caisson.Drivers.Abstractions.Results;

/// <summary>
/// A per-item annotation attached to an otherwise successful <see cref="DriverResult{T}"/>, e.g. one
/// port among many that had no LLDP data. Reuses <see cref="ReasonCode"/> directly rather than
/// duplicating it, since the same reasons (missing LLDP, device unreachable, parse error, ...) apply
/// whether the ambiguity was recorded during correlation or during discovery.
/// </summary>
/// <param name="Severity">Whether the item is a warning (readable is degraded) or an error (unreadable).</param>
/// <param name="ReasonCode">The domain reason code explaining the ambiguity or gap.</param>
/// <param name="EntityRef">
/// An identifier for the affected item within the result, e.g. a port name. Not a database id — the
/// driver has no persistence identity to reference.
/// </param>
/// <param name="Message">A human-readable description of the diagnostic.</param>
public sealed record DriverDiagnostic(
    DriverDiagnosticSeverity Severity,
    ReasonCode ReasonCode,
    string EntityRef,
    string Message);
