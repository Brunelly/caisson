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
