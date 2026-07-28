// Automated accessibility check for the topology page (NFR4: "automated checks pass, e.g. axe").
// Runs axe-core against a fully-rendered page fixture (populated graph, search, legend, details panel
// all present) under vitest/jsdom. `color-contrast` is disabled here — jsdom has no real layout/paint
// engine, so it cannot compute rendered colours; contrast is instead checked by the Playwright e2e a11y
// pass (@axe-core/playwright) against a real browser, where the check is actually meaningful.
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import axe from 'axe-core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { TopologyEntityService } from './services/topology-entity.service';
import { TopologyStateService } from './state/topology-state.service';
import { TopologySignalRService } from './live/topology-signalr.service';
import { deriveTopologyGraph } from './model/topology-graph-model';
import type { TopologyGraphDto } from './model/topology-contracts';
import { TopologyPageComponent } from './topology-page.component';

function fixtureGraph(): TopologyGraphDto {
  return {
    snapshotId: 'snap-1',
    version: 4,
    correlationId: 'corr-1',
    servers: [
      {
        stableKey: 'srv-1',
        hostname: 'srv-01',
        bmcUuid: 'uuid-1',
        nics: [
          {
            stableKey: 'nic-1',
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
            stableKey: 'nic-2',
            name: 'eth1',
            mac: 'aa:bb:cc:dd:ee:02',
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

describe('TopologyPageComponent accessibility', () => {
  let fixture: ComponentFixture<TopologyPageComponent>;

  beforeEach(async () => {
    const stateStub = {
      loading: signal(false),
      error: signal<string | null>(null),
      snapshot: signal({
        snapshotId: 'snap-1',
        version: 4,
        createdAt: '2026-01-01T00:00:00Z',
      } as never),
      discoveryStatus: signal({
        latestJob: { status: 'Succeeded' },
        lastSuccessAt: '2026-01-01T00:00:00Z',
      } as never),
      graph: signal(deriveTopologyGraph(fixtureGraph())),
      selection: signal(null),
      selectionStaleNotice: signal(false),
      connectionStatus: signal('live' as const),
      rackId: signal('rack-1'),
      loadRackTopology: () => undefined,
      selectEntity: () => undefined,
      clearSelection: () => undefined,
    };

    await TestBed.configureTestingModule({
      imports: [TopologyPageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TopologyStateService, useValue: stateStub },
        {
          provide: TopologySignalRService,
          useValue: { connect: () => undefined, disconnect: () => undefined },
        },
        { provide: TopologyEntityService, useValue: { getEntity: () => of({ kind: 'notFound' }) } },
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of({ get: () => 'rack-1' }) },
        },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TopologyPageComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: {
        // jsdom has no layout/paint engine, so contrast can't be computed here — see the Playwright
        // e2e a11y pass for the real, browser-based contrast check.
        'color-contrast': { enabled: false },
      },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
