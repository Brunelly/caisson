namespace Caisson.Api.Contracts;

/// <summary>One authored VLAN catalogue entry on the wire (story #168, AC1).</summary>
public sealed record VlanCatalogueEntryDto(int Id, string Name, string? Description);

/// <summary>
/// One authored per-port access-VLAN intent on the wire (story #168, AC2). <see cref="AccessVlanId"/> is
/// <c>null</c> for "Unchanged/Inherit".
/// </summary>
public sealed record PortAccessIntentDto(string SwitchStableKey, string PortName, int? AccessVlanId);

/// <summary>The request body for both the PUT save endpoint and the POST validate stub.</summary>
public sealed record NetworkIntentSaveRequest(
    IReadOnlyList<VlanCatalogueEntryDto>? VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto>? PortIntents);

/// <summary>
/// A rack's currently saved network intent (GET/PUT response). <see cref="UpdatedAtUtc"/>/
/// <see cref="UpdatedBy"/> are <c>null</c> when no intent has ever been saved for this rack — the GET
/// endpoint returns this default shape rather than 404 (AC1: viewable by a ReadOnly user even before any
/// authoring has happened).
/// </summary>
public sealed record NetworkIntentDto(
    Guid RackId,
    IReadOnlyList<VlanCatalogueEntryDto> VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto> PortIntents,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>One field-scoped validation error (AC1 duplicate-ID / AC2 unknown-VLAN examples).</summary>
public sealed record NetworkIntentValidationErrorDto(string Field, string Message);

/// <summary>
/// The response of the <c>/network-intent/validate</c> stub (story #176): runs the exact same rules the
/// PUT save path enforces and persists nothing. Full pre-flight validation against live discovered
/// inventory is story #170.
/// </summary>
public sealed record NetworkIntentValidationResponse(
    bool IsValid, IReadOnlyList<NetworkIntentValidationErrorDto> Errors);
