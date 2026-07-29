// Fixture data for the dev-only UI harness (see dev-harness.providers.ts). Deliberately mirrors the
// shape used by topology-page.a11y.spec.ts: one confirmed NIC, one unmapped NIC, one ambiguous NIC (two
// candidates) and one unattached (unmapped) port, so every AC4 visual state and the AC3 drill-down
// content (candidates/reason codes/unmapped reason/history) are all reachable in a real browser.
import type {
  DiscoveryStatusDto,
  EntityDetailDto,
  SnapshotMetadataDto,
  TopologyGraphDto,
} from '../topology/model/topology-contracts';
import type {
  DriftApplyJobDetailDto,
  DriftApplyJobStatus,
  DriftApplyJobSummaryDto,
  DriftItemDto,
  DriftReportDetailDto,
  DriftReportSummaryDto,
} from '../drift/model/drift-contracts';

let version = 4;

export function bumpVersion(): number {
  version += 1;
  return version;
}

export function currentVersion(): number {
  return version;
}

export function harnessSnapshotMeta(): SnapshotMetadataDto {
  return {
    snapshotId: `snap-${version}`,
    version,
    triggerType: 'Scheduled',
    createdBy: 'discovery-scheduler',
    source: 'harness',
    sourceVersion: null,
    createdAt: new Date(2026, 0, 1, 12, 0, 0).toISOString(),
    startedAt: null,
    completedAt: null,
    correlationId: 'harness-correlation-1',
    status: 'Succeeded',
    diffSummary: null,
  };
}

export function harnessGraphDto(): TopologyGraphDto {
  return {
    snapshotId: `snap-${version}`,
    version,
    correlationId: 'harness-correlation-1',
    servers: [
      {
        stableKey: 'srv-1',
        hostname: 'srv-01',
        bmcUuid: 'uuid-1',
        nics: [
          {
            stableKey: 'nic-confirmed',
            name: 'eth0',
            mac: 'aa:bb:cc:dd:ee:01',
            bestAttachment: {
              switchStableKey: 'SW-1',
              switchSerial: 'sw1',
              portName: 'ether1',
              confidence: 0.95,
              band: 'High',
              reasonCode: 'MacLearnUnique',
              vlans: [10],
            },
            candidates: [
              {
                switchStableKey: 'SW-1',
                switchSerial: 'sw1',
                portName: 'ether1',
                confidence: 0.95,
                band: 'High',
                reasonCode: 'MacLearnUnique',
                vlans: [10],
              },
            ],
            unmappedReasonCode: null,
          },
          {
            stableKey: 'nic-ambiguous',
            name: 'eth1',
            mac: 'aa:bb:cc:dd:ee:02',
            bestAttachment: {
              switchStableKey: 'SW-1',
              switchSerial: 'sw1',
              portName: 'ether2',
              confidence: 0.6,
              band: 'Medium',
              reasonCode: 'MultipleMacPorts',
              vlans: [20],
            },
            candidates: [
              {
                switchStableKey: 'SW-1',
                switchSerial: 'sw1',
                portName: 'ether2',
                confidence: 0.6,
                band: 'Medium',
                reasonCode: 'MultipleMacPorts',
                vlans: [20],
              },
              {
                switchStableKey: 'SW-1',
                switchSerial: 'sw1',
                portName: 'ether3',
                confidence: 0.55,
                band: 'Medium',
                reasonCode: 'MultipleMacPorts',
                vlans: [20],
              },
            ],
            unmappedReasonCode: null,
          },
          {
            stableKey: 'nic-unmapped',
            name: 'eth2',
            mac: 'aa:bb:cc:dd:ee:03',
            bestAttachment: null,
            candidates: [],
            unmappedReasonCode: 'NotSeenInSwitch',
          },
        ],
      },
    ],
    unmappedPorts: [{ switchStableKey: 'SW-1', switchSerial: 'sw1', portName: 'ether4' }],
  };
}

export function harnessDiscoveryStatus(): DiscoveryStatusDto {
  return {
    rackId: 'rack-1',
    latestJob: {
      jobId: 'job-1',
      rackId: 'rack-1',
      mode: 'Scheduled',
      status: 'Succeeded',
      createdAt: harnessSnapshotMeta().createdAt,
      startedAt: harnessSnapshotMeta().createdAt,
      finishedAt: harnessSnapshotMeta().createdAt,
      triggeredBy: 'scheduler',
      dryRun: false,
      errorCode: null,
      lastSuccessAt: harnessSnapshotMeta().createdAt,
    },
    lastSuccessAt: harnessSnapshotMeta().createdAt,
    scheduleEnabled: true,
    nextRunAt: null,
  };
}

const FIELDS_BY_TYPE: Record<string, Record<string, string | null>> = {
  Server: { bmcType: 'Redfish', bmcAddress: '10.0.0.9', bmcUuid: 'uuid-1', hostname: 'srv-01' },
  Nic: { server: 'srv-01', name: 'eth0', linkState: 'Up' },
  Switch: { serial: 'sw1', managementIp: '10.0.0.1', model: 'CRS326', osVersion: 'RouterOS 7.15' },
  SwitchPort: { switch: 'sw1', isUp: 'true', pvid: '10', taggedVlans: '' },
  Vlan: { name: 'default' },
};

export function harnessEntityDetail(entityType: string, stableKey: string): EntityDetailDto {
  return {
    entityType,
    stableKey,
    latest: FIELDS_BY_TYPE[entityType] ?? null,
    history: [
      {
        entityType,
        entityStableKey: stableKey,
        changeType: 'Added',
        payload: null,
        fromSnapshotId: null,
        toSnapshotId: 'snap-1',
        createdAt: new Date(2025, 11, 30, 9, 0, 0).toISOString(),
        correlationId: 'harness-correlation-0',
      },
      {
        entityType,
        entityStableKey: stableKey,
        changeType: 'Updated',
        payload: null,
        fromSnapshotId: 'snap-1',
        toSnapshotId: `snap-${version}`,
        createdAt: harnessSnapshotMeta().createdAt,
        correlationId: 'harness-correlation-1',
      },
    ],
  };
}

// --- Story #67 drift/apply fixtures ---------------------------------------------------------

const HARNESS_DRIFT_ITEM_ID = 'harness-drift-item-1';
const HARNESS_DRIFT_REPORT_ID = 'harness-drift-report-1';
export const HARNESS_DRIFT_JOB_ID = 'harness-drift-job-1';

let driftJobStatus: DriftApplyJobStatus | null = null;

/** Mutable harness job state — set by the fake DriftApplyService.applyCorrection() and readable by
 * Playwright via window.__harness__.setDriftJobStatus() to drive the polling-fallback scenario. */
export function currentDriftJobStatus(): DriftApplyJobStatus | null {
  return driftJobStatus;
}

export function setDriftJobStatus(status: DriftApplyJobStatus | null): void {
  driftJobStatus = status;
}

export function harnessDriftItem(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: HARNESS_DRIFT_ITEM_ID,
    driftReportId: HARNESS_DRIFT_REPORT_ID,
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack-1|SW-1|ether2',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch on SW-1/ether2 — expected VLAN 200, observed VLAN 100.',
    details: { switchName: 'SW-1', portName: 'ether2' },
    createdAt: harnessSnapshotMeta().createdAt,
    ...overrides,
  };
}

export function harnessDriftReportSummary(): DriftReportSummaryDto {
  return {
    driftReportId: HARNESS_DRIFT_REPORT_ID,
    desiredRevisionId: 'harness-revision-1',
    observedSnapshotId: `snap-${version}`,
    computedAt: harnessSnapshotMeta().createdAt,
    computationVersion: 1,
    totalItems: 1,
    countsBySeverity: { High: 1, Medium: 0, Low: 0 },
    hasAmbiguities: false,
    isTruncated: false,
    status: 'Completed',
    errorSummary: null,
  };
}

export function harnessDriftReportDetail(
  items: DriftItemDto[] = [harnessDriftItem()],
): DriftReportDetailDto {
  return { report: harnessDriftReportSummary(), items: { items, nextCursor: null } };
}

export function harnessDriftApplyJobSummary(
  overrides: Partial<DriftApplyJobSummaryDto> = {},
): DriftApplyJobSummaryDto {
  return {
    jobId: HARNESS_DRIFT_JOB_ID,
    rackId: 'rack-1',
    driftItemId: HARNESS_DRIFT_ITEM_ID,
    status: driftJobStatus ?? 'Pending',
    requestedAt: harnessSnapshotMeta().createdAt,
    finishedAt: null,
    requestedBy: 'harness-user',
    errorCategory: null,
    errorCode: null,
    ...overrides,
  };
}

export function harnessDriftApplyJobDetail(
  overrides: Partial<DriftApplyJobDetailDto> = {},
): DriftApplyJobDetailDto {
  return {
    jobId: HARNESS_DRIFT_JOB_ID,
    rackId: 'rack-1',
    driftItemId: HARNESS_DRIFT_ITEM_ID,
    status: driftJobStatus ?? 'Pending',
    requestedAt: harnessSnapshotMeta().createdAt,
    claimedAt: harnessSnapshotMeta().createdAt,
    finishedAt: null,
    requestedBy: 'harness-user',
    actorType: 'User',
    correlationId: 'harness-correlation-drift-1',
    attemptCount: 1,
    currentStep: 'Revalidating',
    switchDeviceKey: 'SW-1',
    portName: 'ether2',
    desiredVlanId: 200,
    deviceReasonCode: null,
    deviceConfirmed: null,
    beforeState: '100',
    afterState: null,
    errorCategory: null,
    errorCode: null,
    errorMessage: null,
    steps: [],
    ...overrides,
  };
}
