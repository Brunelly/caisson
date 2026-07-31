namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// The severity bucket a <see cref="PreflightIssue"/> falls into (story #170, AC4 "issues are grouped by
/// severity"). Only two levels exist on the wire: <see cref="Error"/> blocks PR creation; <see cref="Warning"/>
/// (including safety notices) is non-blocking and requires explicit acknowledgement instead.
/// </summary>
public enum PreflightSeverity
{
    /// <summary>A blocking problem — schema or semantic failure that must be fixed before a PR can be created.</summary>
    Error,

    /// <summary>A non-blocking advisory — including safety guardrail warnings that require acknowledgement.</summary>
    Warning,
}

/// <summary>
/// One pre-flight validation issue (story #170). A pure, deterministic value carrier the API projects onto
/// <c>ValidationIssueDto</c> verbatim. Every issue is field-addressable (NFR1) via a canonical RFC 6901
/// JSON Pointer <see cref="FieldPath"/> plus the bracket-form <see cref="UiPath"/> the Angular editor maps
/// to a control, and carries a stable machine-readable <see cref="Code"/> and an <see cref="EntityRef"/> so
/// automation and re-runs stay consistent (NFR3, AC4).
/// </summary>
/// <param name="Severity">Whether this issue blocks (Error) or merely warns (Warning).</param>
/// <param name="Code">
/// A stable, dotted, machine-readable code (e.g. <c>schema.vlanIdRange</c>, <c>semantic.switchNotFound</c>,
/// <c>safety.uplinkPort</c>). Never localized; the UI keys off it, never parses <see cref="Message"/>.
/// </param>
/// <param name="Message">A user-friendly, display-ready message (AC1 "suitable for display in the UI").</param>
/// <param name="FieldPath">The canonical RFC 6901 JSON Pointer, e.g. <c>/vlanCatalogue/2/id</c>.</param>
/// <param name="UiPath">
/// The bracket/dot editor path the Angular components filter on, e.g. <c>vlanCatalogue.vlans[2].id</c> or
/// <c>ports["switchA/ether5"].accessVlanId</c>. Optional (null for rack-scoped issues with no single field).
/// </param>
/// <param name="EntityRef">The rack/switch/port/VLAN entity this issue concerns.</param>
/// <param name="HelpUrl">Optional deep link to remediation guidance.</param>
/// <param name="Details">Optional small, secret-free key/value bag (e.g. <c>reason = heuristic-derived</c>).</param>
public sealed record PreflightIssue(
    PreflightSeverity Severity,
    string Code,
    string Message,
    string FieldPath,
    string? UiPath,
    EntityRef EntityRef,
    string? HelpUrl = null,
    IReadOnlyDictionary<string, string>? Details = null);
