import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { DriftItemDto } from '../../drift/model/drift-contracts';
import { DriftReportService } from '../../drift/services/drift-report.service';
import type { SnapshotDetailDto, TopologyGraphDto } from '../model/topology-contracts';
import { deriveTopologyGraph, portNodeId } from '../model/topology-graph-model';
import { DiscoveryStatusService } from '../services/discovery-status.service';
import { TopologySnapshotService } from '../services/topology-snapshot.service';
import { TopologyStateService } from './topology-state.service';

function driftItem(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'drift-item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|SW-1|ether1',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch',
    details: { switchName: 'SW-1', portName: 'ether1' },
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function graphDto(): TopologyGraphDto {
  return {
    snapshotId: 'snap-1',
    version: 1,
    correlationId: 'corr-1',
    servers: [
      {
        stableKey: 'srv-1',
        hostname: 'srv-01',
        bmcUuid: null,
        nics: [
          {
            stableKey: 'nic-1',
            name: 'eth0',
            mac: 'aabbccddee01',
            bestAttachment: {
              switchStableKey: 'SW-1|sw1',
              switchSerial: 'sw1',
              portName: 'ether1',
              confidence: 0.95,
              band: 'High',
              reasonCode: 'MacLearnUnique',
              vlans: [10],
            },
            candidates: [],
            unmappedReasonCode: null,
          },
        ],
      },
    ],
    unmappedPorts: [],
  };
}

function snapshotDetail(): SnapshotDetailDto {
  return {
    snapshot: {
      snapshotId: 'snap-1',
      version: 1,
      triggerType: 'Manual',
      createdBy: 'test',
      source: 'test',
      sourceVersion: null,
      createdAt: '2026-01-01T00:00:00Z',
      startedAt: null,
      completedAt: null,
      correlationId: 'corr-1',
      status: 'Completed',
      diffSummary: null,
    },
    graph: graphDto(),
  };
}

describe('TopologyStateService drift wiring (story #67)', () => {
  function setup(getLatestDrift: ReturnType<typeof vi.fn>) {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: TopologySnapshotService,
          useValue: { getLatest: vi.fn(() => of({ kind: 'ok', value: snapshotDetail() })) },
        },
        {
          provide: DiscoveryStatusService,
          useValue: { getStatus: vi.fn(() => of({ kind: 'ok', value: null })) },
        },
        { provide: DriftReportService, useValue: { getLatest: getLatestDrift } },
      ],
    });
    return TestBed.inject(TopologyStateService);
  }

  it('extends loadRackTopology to fetch drift and derives a non-empty overlay for a matching AccessVlanMismatch item', () => {
    const getLatestDrift = vi.fn(() =>
      of({
        kind: 'ok',
        value: {
          report: { driftReportId: 'report-1' },
          items: { items: [driftItem()], nextCursor: null },
        },
      }),
    );
    const state = setup(getLatestDrift);

    state.loadRackTopology('rack-1');

    expect(getLatestDrift).toHaveBeenCalledWith('rack-1');
    expect(state.driftItems()).toEqual([driftItem()]);
    const overlay = state.driftOverlay();
    expect(overlay.get(portNodeId('SW-1|sw1', 'ether1'))).toEqual({
      driftItemId: 'drift-item-1',
      driftType: 'AccessVlanMismatch',
      severity: 'High',
    });
  });

  it('is a complete no-op (empty overlay, unchanged graph) when there are no drift items — do-not-regress #10', () => {
    const getLatestDrift = vi.fn(() =>
      of({
        kind: 'ok',
        value: { report: { driftReportId: 'report-1' }, items: { items: [], nextCursor: null } },
      }),
    );
    const state = setup(getLatestDrift);

    state.loadRackTopology('rack-1');

    expect(state.driftOverlay().size).toBe(0);
    expect(state.graph()).toEqual(deriveTopologyGraph(graphDto()));
  });

  it('never fails the topology load when the drift fetch itself fails (drift is independent of the read-only map)', () => {
    const getLatestDrift = vi.fn(() => of({ kind: 'forbidden' }));
    const state = setup(getLatestDrift);

    state.loadRackTopology('rack-1');

    expect(state.error()).toBeNull();
    expect(state.graph()).toBeTruthy();
    expect(state.driftOverlay().size).toBe(0);
  });

  it('applyRefreshedSnapshot re-applies the overlay against the new graph without re-fetching drift', () => {
    const getLatestDrift = vi.fn(() =>
      of({
        kind: 'ok',
        value: {
          report: { driftReportId: 'report-1' },
          items: { items: [driftItem()], nextCursor: null },
        },
      }),
    );
    const state = setup(getLatestDrift);
    state.loadRackTopology('rack-1');
    getLatestDrift.mockClear();

    const refreshedGraph = deriveTopologyGraph(graphDto());
    state.applyRefreshedSnapshot(snapshotDetail().snapshot, refreshedGraph);

    expect(getLatestDrift).not.toHaveBeenCalled();
    expect(state.driftOverlay().get(portNodeId('SW-1|sw1', 'ether1'))?.driftItemId).toBe(
      'drift-item-1',
    );
  });
});
