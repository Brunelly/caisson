// TypeScript mirror of Caisson.Api/Contracts/PreflightContracts.cs, field for field, in the camelCase the
// API's default System.Text.Json serialization produces on the wire (story #170). Keep this file in
// lock-step with the C# records — it is the only place the pre-flight wire shape is declared.
import type { PortAccessIntentDto, VlanCatalogueEntryDto } from './network-intent-contracts';

/** Severity buckets on the wire. Safety notices are Warnings with a `safety.*` code. */
export type PreflightSeverity = 'error' | 'warning';

/** The machine-readable entity an issue concerns (mirrors EntityRefDto). */
export interface EntityRef {
  kind: 'rack' | 'switch' | 'port' | 'vlan';
  rackId: string;
  switchStableKey: string | null;
  portName: string | null;
  vlanId: number | null;
}

/** One pre-flight issue (mirrors ValidationIssueDto). `fieldPath` is a canonical RFC 6901 JSON Pointer;
 * `uiPath` is the bracket/dot editor path the components map to a control. */
export interface ValidationIssue {
  severity: PreflightSeverity;
  code: string;
  message: string;
  fieldPath: string;
  uiPath: string | null;
  entityRef: EntityRef;
  helpUrl: string | null;
  details: Record<string, string> | null;
}

/** The grouped pre-flight validation response (mirrors PreflightValidationResponse). */
export interface PreflightValidationResponse {
  validationRunId: string;
  isValid: boolean;
  canCreatePr: boolean;
  errors: ValidationIssue[];
  warnings: ValidationIssue[];
  validatedAtUtc: string;
  topologySnapshotId: string | null;
}

/** The pre-flight validate request body (mirrors PreflightValidateRequest). */
export interface PreflightValidateRequest {
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
}

/** The gated PR-creation request body (mirrors CreatePrRequest). */
export interface CreatePrRequest {
  validationRunId: string;
  acknowledgedWarningCodes: string[];
  vlanCatalogue: VlanCatalogueEntryDto[];
  portIntents: PortAccessIntentDto[];
}

/** The PR-creation response (mirrors CreatePrResponse). `pullRequestUrl` is null until #172 lands. */
export interface CreatePrResponse {
  validationRunId: string;
  status: string;
  detail: string;
  pullRequestUrl: string | null;
}

/** Whether an issue is a safety notice (rendered in its own, stronger-treatment group). */
export function isSafetyIssue(issue: ValidationIssue): boolean {
  return issue.code.startsWith('safety.');
}
