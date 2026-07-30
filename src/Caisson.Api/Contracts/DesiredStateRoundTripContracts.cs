namespace Caisson.Api.Contracts;

/// <summary>The POST <c>parse</c> request: the raw YAML document to import (story #169, AC4).</summary>
public sealed record DesiredStateParseRequest(string? Yaml);

/// <summary>One preserved unknown YAML block on the wire (byte-for-byte round-trip, AC2).</summary>
public sealed record PreservedYamlBlockDto(string AnchorPath, string RawYamlText, string Checksum);

/// <summary>The v1 UI-supported subset on the wire: rack slug, VLAN catalogue, per-port access-VLAN intents.</summary>
public sealed record SupportedDesiredStateModelDto(
    string RackSlug,
    IReadOnlyList<VlanCatalogueEntryDto> VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto> PortIntents);

/// <summary>The POST <c>parse</c> success response: the full round-trip envelope.</summary>
public sealed record DesiredStateRoundTripEnvelopeDto(
    SupportedDesiredStateModelDto SupportedModel,
    IReadOnlyList<PreservedYamlBlockDto> UnknownBlocks,
    IReadOnlyList<string> Warnings,
    int SchemaVersion);

/// <summary>One parse issue on the wire: a dotted document path plus a message and optional line/column.</summary>
public sealed record DesiredStateImportIssueDto(string Path, string Message, int? Line, int? Column);

/// <summary>
/// The POST <c>render</c> request: the supported model (VLAN catalogue + port intents) plus any preserved
/// blocks and warnings to carry through. The rack slug for <c>metadata.rackSlug</c> is resolved server-side
/// from the rack, never trusted from the client, so it is not part of this request.
/// </summary>
public sealed record DesiredStateRenderRequest(
    IReadOnlyList<VlanCatalogueEntryDto>? VlanCatalogue,
    IReadOnlyList<PortAccessIntentDto>? PortIntents,
    IReadOnlyList<PreservedYamlBlockDto>? UnknownBlocks,
    IReadOnlyList<string>? Warnings,
    int? SchemaVersion);

/// <summary>The POST <c>render</c> response: the canonical UTF-8 YAML plus any non-fatal warnings.</summary>
public sealed record DesiredStateRenderResponse(string Yaml, IReadOnlyList<string> Warnings);
