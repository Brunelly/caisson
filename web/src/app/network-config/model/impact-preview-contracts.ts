// TypeScript mirror of Caisson.Api/Contracts/ImpactPreviewContracts.cs, field for field, in camelCase
// (story #171). Keep this file in lock-step with the C# contract. Reuses the shared EntityRef shape.
import type { EntityRef } from './preflight-validation-contracts';

/** One field snapshot within a change's before/after state. */
export interface ImpactChangeField {
  field: string;
  value: string | null;
}

/**
 * One semantic change on the wire. `kind` is Added|Removed|Modified, `category` is Vlan|Port. `existsInTopology`
 * drives whether the UI renders a topology deep link (true) or a non-blocking "not found in topology" badge.
 */
export interface ImpactChange {
  kind: 'Added' | 'Removed' | 'Modified';
  category: 'Vlan' | 'Port';
  changeId: string;
  summary: string;
  entityRef: EntityRef;
  existsInTopology: boolean;
  before: ImpactChangeField[];
  after: ImpactChangeField[];
}

/** The impact-preview response: cache identity + raw unified diff + structured summary grouped by VLANs / ports. */
export interface ImpactPreviewResponse {
  candidateId: string;
  candidateSha256: string;
  baselineSha256: string;
  baselineRevisionId: string;
  baselineCommitSha: string | null;
  cacheHit: boolean;
  createdAtUtc: string;
  rawUnifiedDiff: string;
  vlanChanges: ImpactChange[];
  portChanges: ImpactChange[];
}

/** The 409 body when the rack has no ingested baseline revision (AC5). */
export interface MissingBaselineResponse {
  reasonCode: string;
  message: string;
}

/** One import issue surfaced on a 400 (AC5) — mirrors DesiredStateImportIssueDto. */
export interface ImpactPreviewIssue {
  path: string;
  message: string;
  line: number | null;
  column: number | null;
}
