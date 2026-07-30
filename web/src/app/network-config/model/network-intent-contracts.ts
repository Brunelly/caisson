// TypeScript mirror of Caisson.Api/Contracts/NetworkIntentContracts.cs, field for field, in the
// camelCase the API's default System.Text.Json serialization produces on the wire. Keep this file in
// lock-step with the C# records — it is the only place the wire shape is declared.

export interface VlanCatalogueEntryDto {
  id: number;
  name: string;
  description: string | null;
}

/** `accessVlanId` is `null` for "Unchanged/Inherit" (story #168, AC2) — never a separate boolean flag. */
export interface PortAccessIntentDto {
  switchStableKey: string;
  portName: string;
  accessVlanId: number | null;
}

export interface NetworkIntentSaveRequest {
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
}

/** `updatedAtUtc`/`updatedBy` are `null` when no intent has ever been saved for this rack (AC1: GET
 * returns this default shape, never 404, so a Read Only user can view it before anyone has authored anything). */
export interface NetworkIntentDto {
  rackId: string;
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
  updatedAtUtc: string | null;
  updatedBy: string | null;
}

export interface NetworkIntentValidationErrorDto {
  field: string;
  message: string;
}

export interface NetworkIntentValidationResponse {
  isValid: boolean;
  errors: NetworkIntentValidationErrorDto[];
}

// --- Story #169: desired-state YAML round-trip. TypeScript mirror of
// Caisson.Api/Contracts/DesiredStateRoundTripContracts.cs; keep in lock-step.

/** One unknown/unsupported YAML block preserved byte-for-byte for lossless round-trip (AC2). */
export interface PreservedYamlBlockDto {
  anchorPath: string;
  rawYamlText: string;
  checksum: string;
}

/** The v1 UI-supported subset extracted from a document: rack slug + VLAN catalogue + port intents. */
export interface SupportedDesiredStateModelDto {
  rackSlug: string;
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
}

/** The POST `parse` success response — the full round-trip envelope. */
export interface DesiredStateRoundTripEnvelopeDto {
  supportedModel: SupportedDesiredStateModelDto;
  unknownBlocks: PreservedYamlBlockDto[];
  warnings: string[];
  schemaVersion: number;
}

/** One parse issue on the wire: a dotted document path plus a message and optional line/column (AC4). */
export interface DesiredStateImportIssueDto {
  path: string;
  message: string;
  line: number | null;
  column: number | null;
}

/** The POST `parse` request body. */
export interface DesiredStateParseRequest {
  yaml: string;
}

/** The POST `render` request body — the rack slug is resolved server-side, never sent by the client. */
export interface DesiredStateRenderRequest {
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
  unknownBlocks: PreservedYamlBlockDto[];
  warnings: string[];
  schemaVersion: number | null;
}

/** The POST `render` response — canonical UTF-8, LF-only YAML plus any non-fatal warnings. */
export interface DesiredStateRenderResponse {
  yaml: string;
  warnings: string[];
}
