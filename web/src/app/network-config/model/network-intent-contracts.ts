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
