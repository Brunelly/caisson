using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;

namespace Caisson.Api.Contracts;

/// <summary>
/// The pre-flight validate request (story #170, AC1): the authored candidate to validate. Reuses the
/// existing <see cref="VlanCatalogueEntryDto"/>/<see cref="PortAccessIntentDto"/> wire shapes so the
/// authoring UI sends exactly what it already models.
/// </summary>
public sealed record PreflightValidateRequest(
    IReadOnlyList<VlanCatalogueEntryDto>? VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto>? PortIntents);

/// <summary>The machine-readable entity a <see cref="ValidationIssueDto"/> concerns (rack/switch/port/vlan).</summary>
public sealed record EntityRefDto(
    string Kind,
    Guid RackId,
    string? SwitchStableKey,
    string? PortName,
    int? VlanId);

/// <summary>
/// One pre-flight issue on the wire (story #170, NFR1). Mirrors <see cref="PreflightIssue"/>: a stable
/// <see cref="Code"/>, a display <see cref="Message"/>, the canonical RFC 6901 <see cref="FieldPath"/>, the
/// bracket-form <see cref="UiPath"/> the editor maps to a control, an <see cref="EntityRef"/>, and optional
/// help/details. <see cref="Severity"/> is a lowercase string (<c>error</c>|<c>warning</c>).
/// </summary>
public sealed record ValidationIssueDto(
    string Severity,
    string Code,
    string Message,
    string FieldPath,
    string? UiPath,
    EntityRefDto EntityRef,
    string? HelpUrl,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>
/// The grouped pre-flight validation response (story #170, AC4). Carries the server-issued, content-bound
/// <see cref="ValidationRunId"/>, the errors/warnings grouped by severity, and the topology snapshot the
/// port resolution ran against. <see cref="IsValid"/> is true when there are no errors; <see cref="CanCreatePr"/>
/// is true only when there are neither errors nor warnings (warnings still require acknowledgement).
/// </summary>
public sealed record PreflightValidationResponse(
    string ValidationRunId,
    bool IsValid,
    bool CanCreatePr,
    IReadOnlyList<ValidationIssueDto> Errors,
    IReadOnlyList<ValidationIssueDto> Warnings,
    DateTime ValidatedAtUtc,
    Guid? TopologySnapshotId);

/// <summary>
/// The gated PR-creation request (story #170, AC5 / Q3 answer). Carries the full candidate (never trusted
/// counts/flags), the server-issued <see cref="ValidationRunId"/> the client last saw, and the warning
/// codes the user explicitly acknowledged. The server re-validates and re-derives the run id before acting.
/// </summary>
public sealed record CreatePrRequest(
    string? ValidationRunId,
    IReadOnlyList<string>? AcknowledgedWarningCodes,
    IReadOnlyList<VlanCatalogueEntryDto>? VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto>? PortIntents);

/// <summary>
/// The PR-creation response (story #170). The real forge/PR pipeline is deferred to #172; today the gate
/// passes and the stub publisher accepts the request without any git write. <see cref="Status"/> is a
/// stable string; <see cref="PullRequestUrl"/> is null until #172 lands.
/// </summary>
public sealed record CreatePrResponse(
    string ValidationRunId,
    string Status,
    string Detail,
    string? PullRequestUrl);

/// <summary>Maps the pure <see cref="PreflightIssue"/> set onto the grouped wire response.</summary>
public static class PreflightContractMappers
{
    /// <summary>Projects an authored candidate request onto the domain value carriers.</summary>
    public static (IReadOnlyList<VlanCatalogueEntry> VlanCatalogue, IReadOnlyList<PortAccessIntent> PortIntents)
        ToDomain(
            IReadOnlyList<VlanCatalogueEntryDto>? vlanCatalogue,
            IReadOnlyList<PortAccessIntentDto>? portIntents)
    {
        var vlans = (vlanCatalogue ?? Array.Empty<VlanCatalogueEntryDto>())
            .Select(v => new VlanCatalogueEntry(v.Id, v.Name, v.Description))
            .ToList();
        var ports = (portIntents ?? Array.Empty<PortAccessIntentDto>())
            .Select(p => new PortAccessIntent(p.SwitchStableKey, p.PortName, p.AccessVlanId))
            .ToList();
        return (vlans, ports);
    }

    /// <summary>Builds the grouped-by-severity response from the pure issue set + run metadata.</summary>
    public static PreflightValidationResponse ToResponse(
        string validationRunId,
        IReadOnlyList<PreflightIssue> issues,
        DateTime validatedAtUtc,
        Guid? topologySnapshotId)
    {
        var errors = issues
            .Where(i => i.Severity == PreflightSeverity.Error)
            .Select(ToDto)
            .ToList();
        var warnings = issues
            .Where(i => i.Severity == PreflightSeverity.Warning)
            .Select(ToDto)
            .ToList();

        return new PreflightValidationResponse(
            validationRunId,
            IsValid: errors.Count == 0,
            CanCreatePr: errors.Count == 0 && warnings.Count == 0,
            errors,
            warnings,
            validatedAtUtc,
            topologySnapshotId);
    }

    /// <summary>Maps one pure issue onto its wire DTO.</summary>
    public static ValidationIssueDto ToDto(PreflightIssue issue)
        => new(
            issue.Severity.ToString().ToLowerInvariant(),
            issue.Code,
            issue.Message,
            issue.FieldPath,
            issue.UiPath,
            new EntityRefDto(
                issue.EntityRef.Kind.ToString().ToLowerInvariant(),
                issue.EntityRef.RackId,
                issue.EntityRef.SwitchStableKey,
                issue.EntityRef.PortName,
                issue.EntityRef.VlanId),
            issue.HelpUrl,
            issue.Details);
}
