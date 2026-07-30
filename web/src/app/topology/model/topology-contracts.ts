// TypeScript mirror of Caisson.Api/Contracts/TopologyContracts.cs and DiscoveryContracts.cs, field for
// field, in the camelCase the API's default System.Text.Json serialization produces on the wire. Keep
// this file in lock-step with the C# records — it is the only place the wire shape is declared.

export interface PagedResult<T> {
  items: T[];
  nextCursor: string | null;
}

export interface SnapshotMetadataDto {
  snapshotId: string;
  version: number;
  triggerType: string;
  createdBy: string;
  source: string;
  sourceVersion: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  correlationId: string;
  status: string;
  diffSummary: unknown | null;
}

export interface SnapshotDetailDto {
  snapshot: SnapshotMetadataDto;
  graph: TopologyGraphDto;
}

export interface TopologyGraphDto {
  snapshotId: string;
  version: number;
  correlationId: string;
  servers: ServerNodeDto[];
  unmappedPorts: UnmappedPortDto[];
  /** Story #168: a flat switch → ports inventory, additive alongside the NIC-rooted graph above — the
   * Network Config Port Intent screen's source for "every discovered port on every switch". Optional so
   * every pre-existing graph fixture/test literal (written before this field existed) keeps compiling;
   * a real API response always includes it, so consumers should treat a missing value as `[]`. */
  switches?: SwitchInventoryDto[];
}

export interface ServerNodeDto {
  stableKey: string;
  hostname: string | null;
  bmcUuid: string | null;
  nics: NicNodeDto[];
}

/** `unmappedReasonCode` is set only when `bestAttachment` is null (backend story #10 step 1 addition). */
export interface NicNodeDto {
  stableKey: string;
  name: string;
  mac: string;
  bestAttachment: PortAttachmentDto | null;
  candidates: PortAttachmentDto[];
  unmappedReasonCode: string | null;
}

export interface PortAttachmentDto {
  switchStableKey: string;
  switchSerial: string | null;
  portName: string;
  confidence: number;
  band: string;
  reasonCode: string;
  vlans: number[];
}

export interface UnmappedPortDto {
  switchStableKey: string;
  switchSerial: string | null;
  portName: string;
}

/** A discovered switch and its full flat port inventory (story #168) — drives Network Config's Port
 * Intent screen. Existing NIC-rooted graph consumers ignore this field. */
export interface SwitchInventoryDto {
  stableKey: string;
  serial: string | null;
  name: string;
  ports: SwitchPortInventoryDto[];
}

export interface SwitchPortInventoryDto {
  stableKey: string;
  portName: string;
}

export interface EntityDiffDto {
  entityType: string;
  entityStableKey: string;
  changeType: string;
  payload: unknown;
  fromSnapshotId: string | null;
  toSnapshotId: string | null;
  createdAt: string | null;
  correlationId: string;
}

export interface SnapshotDiffDto {
  fromSnapshotId: string;
  toSnapshotId: string;
  changeSummary: unknown;
  diffs: EntityDiffDto[];
}

export interface EntityDetailDto {
  entityType: string;
  stableKey: string;
  latest: Record<string, string | null> | null;
  history: EntityDiffDto[];
}

export interface AuditEventDto {
  auditEventId: string;
  rackId: string | null;
  snapshotId: string | null;
  occurredAt: string;
  actorType: string;
  actorId: string;
  action: string;
  targetType: string;
  targetId: string | null;
  result: string;
  correlationId: string;
}

export interface DiscoveryJobSummaryDto {
  jobId: string;
  rackId: string;
  mode: string;
  status: string;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  triggeredBy: string;
  dryRun: boolean;
  errorCode: string | null;
  lastSuccessAt: string | null;
}

export interface DiscoveryStatusDto {
  rackId: string;
  latestJob: DiscoveryJobSummaryDto | null;
  lastSuccessAt: string | null;
  scheduleEnabled: boolean;
  nextRunAt: string | null;
}
