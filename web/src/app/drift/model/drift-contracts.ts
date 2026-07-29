// TypeScript mirror of Caisson.Api/Contracts/DriftContracts.cs, DriftApplyContracts.cs, the
// Caisson.Domain.Enums drift/job-status enums, and Caisson.Infrastructure.LiveUpdates.
// DriftApplyJobStatusChangedEvent, field for field, in the camelCase the API's default
// System.Text.Json serialization produces on the wire. Keep this file in lock-step with the C#
// records/enums — it is the only place the wire shape is declared. `Guid`/`DateTime`/`DateTimeOffset`
// fields are always `string` here (JSON serializes them as strings); `severity`/`driftType`/
// `subjectType`/job `status` are plain strings on the wire but typed as string-literal unions below so
// callers get compile-time exhaustiveness instead of a bare `string`.
import type { PagedResult } from '../../topology/model/topology-contracts';

/** Mirrors Caisson.Domain.Enums.DriftSeverity. */
export type DriftSeverity = 'Low' | 'Medium' | 'High';

/** Mirrors Caisson.Domain.Enums.DriftType. */
export type DriftType =
  | 'MissingDesiredEntity'
  | 'ExtraObservedEntity'
  | 'AccessVlanMismatch'
  | 'UnexpectedTrunkConfig'
  | 'UnexpectedNeighbour'
  | 'UnknownTopologyMapping';

/** Mirrors Caisson.Domain.Enums.DriftSubjectType. */
export type DriftSubjectType = 'SwitchPort' | 'ServerNic' | 'LogicalLink';

/** Mirrors Caisson.Domain.Enums.DriftApplyJobStatus. */
export type DriftApplyJobStatus =
  | 'Pending'
  | 'Claimed'
  | 'Revalidating'
  | 'Executing'
  | 'Completed'
  | 'Failed'
  | 'StaleDrift'
  | 'Canceled';

/** The `DriftApplyJobStatus` values that terminate the job — no further live/polled updates follow. */
export const TERMINAL_DRIFT_APPLY_JOB_STATUSES: readonly DriftApplyJobStatus[] = [
  'Completed',
  'Failed',
  'StaleDrift',
  'Canceled',
];

export function isTerminalDriftApplyJobStatus(status: DriftApplyJobStatus): boolean {
  return TERMINAL_DRIFT_APPLY_JOB_STATUSES.includes(status);
}

/** Mirrors Caisson.Api.Contracts.DriftReportSummaryDto. `countsBySeverity` is a free-form JsonElement
 * bag on the wire (a severity-keyed count dictionary in practice) — kept as `unknown`, matching the
 * `diffSummary`/`details` convention in topology-contracts.ts, since callers must not assume its shape. */
export interface DriftReportSummaryDto {
  driftReportId: string;
  desiredRevisionId: string;
  observedSnapshotId: string;
  computedAt: string;
  computationVersion: number;
  totalItems: number;
  countsBySeverity: unknown;
  hasAmbiguities: boolean;
  isTruncated: boolean;
  status: string;
  errorSummary: string | null;
}

/** Mirrors Caisson.Api.Contracts.DriftReportDetailDto. */
export interface DriftReportDetailDto {
  report: DriftReportSummaryDto;
  items: PagedResult<DriftItemDto>;
}

/** Mirrors Caisson.Api.Contracts.DriftItemDto. `subjectKey` is a versioned, opaque identifier
 * (Caisson.Domain.Drift.Diffing.DriftSubjectKeys, ADR 0029) — never parse it client-side, only display
 * it verbatim. `details` is a free-form JsonElement bag (ADR 0032: AccessVlanMismatch items additively
 * carry `{switchName, portName}` here for the topology overlay join — see
 * drift/model/drift-topology-overlay.ts); kept as `unknown` since its shape varies by driftType. */
export interface DriftItemDto {
  driftItemId: string;
  driftReportId: string;
  driftType: DriftType;
  severity: DriftSeverity;
  actionable: boolean;
  subjectType: DriftSubjectType;
  subjectKey: string;
  expectedValue: string | null;
  actualValue: string | null;
  why: string;
  details: unknown | null;
  createdAt: string;
}

/** Mirrors Caisson.Api.Contracts.ApplyDriftCorrectionRequest. */
export interface ApplyDriftCorrectionRequest {
  driftItemId: string;
}

/** Mirrors Caisson.Api.Contracts.ApplyDriftCorrectionResponse. */
export interface ApplyDriftCorrectionResponse {
  jobId: string;
}

/** Mirrors Caisson.Api.Contracts.DriftApplyJobSummaryDto. */
export interface DriftApplyJobSummaryDto {
  jobId: string;
  rackId: string;
  driftItemId: string;
  status: DriftApplyJobStatus;
  requestedAt: string;
  finishedAt: string | null;
  requestedBy: string;
  errorCategory: string | null;
  errorCode: string | null;
}

/** Mirrors Caisson.Api.Contracts.DriftApplyStepDto. */
export interface DriftApplyStepDto {
  stepName: string;
  status: string;
  attemptCount: number;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  errorCode: string | null;
  errorMessage: string | null;
}

/** Mirrors Caisson.Api.Contracts.DriftApplyJobDetailDto. */
export interface DriftApplyJobDetailDto {
  jobId: string;
  rackId: string;
  driftItemId: string;
  status: DriftApplyJobStatus;
  requestedAt: string;
  claimedAt: string | null;
  finishedAt: string | null;
  requestedBy: string;
  actorType: string;
  correlationId: string;
  attemptCount: number;
  currentStep: string | null;
  switchDeviceKey: string | null;
  portName: string | null;
  desiredVlanId: number | null;
  deviceReasonCode: string | null;
  deviceConfirmed: boolean | null;
  beforeState: string | null;
  afterState: string | null;
  errorCategory: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  steps: DriftApplyStepDto[];
}

/** Mirrors Caisson.Infrastructure.LiveUpdates.DriftApplyJobStatusChangedEvent — the SignalR payload
 * relayed over the SAME TopologyHub/`/hubs/topology` connection and per-rack group as
 * SnapshotUpdated/DiscoveryJobStatusChanged (ADR 0032: no new hub, no new channel). Unlike those two
 * events this one carries no `eventId`; watermark dedup keys on `${jobId}:${seq}` instead (see
 * drift/live/drift-apply-job-status.service.ts). */
export interface DriftApplyJobStatusChangedEvent {
  rackId: string;
  jobId: string;
  status: DriftApplyJobStatus;
  previousStatus: DriftApplyJobStatus | null;
  currentStep: string | null;
  reasonCode: string | null;
  errorCode: string | null;
  timestamp: string;
  seq: number;
  correlationId: string;
}
