// Behavioural coverage for TopologyPageComponent's selection wiring and rack-change reactivity,
// complementing the accessibility pass in topology-page.a11y.spec.ts.
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TopologyGraphComponent } from './graph/topology-graph.component';
import { TopologyEntityService } from './services/topology-entity.service';
import { TopologyStateService } from './state/topology-state.service';
import { TopologySignalRService } from './live/topology-signalr.service';
import type { TopologyGraphModel, TopologyGraphNode } from './model/topology-graph-model';
import { TopologyPageComponent } from './topology-page.component';

function graphFixture(): TopologyGraphModel {
  const nicNode: TopologyGraphNode = {
    id: 'nic:srv-1:eth0',
    type: 'nic',
    label: 'eth0',
    stableKey: 'nic:srv-1:eth0',
    mac: 'aabbccddee01',
    state: 'confirmed',
    unmappedReasonCode: null,
    candidates: [],
  } as unknown as TopologyGraphNode;

  return {
    snapshotId: 'snap-1',
    version: 3,
    nodes: {
      servers: [],
      nics: [nicNode as never],
      switches: [],
      ports: [],
      vlans: [],
    },
    edges: [
      {
        id: 'e1',
        source: 'srv-1',
        target: 'nic:srv-1:eth0',
        kind: 'server-nic',
        state: 'confirmed',
      },
    ],
  };
}

describe('TopologyPageComponent', () => {
  let fixture: ComponentFixture<TopologyPageComponent>;
  let selectEntity: ReturnType<typeof vi.fn>;
  let loadRackTopology: ReturnType<typeof vi.fn>;
  let signalRConnect: ReturnType<typeof vi.fn>;
  let paramMap$: Subject<{ get(key: string): string | null }>;
  let queryParamMap$: Subject<ReturnType<typeof convertToParamMap>>;

  beforeEach(async () => {
    selectEntity = vi.fn();
    loadRackTopology = vi.fn();
    signalRConnect = vi.fn();
    paramMap$ = new Subject();
    queryParamMap$ = new Subject();

    const stateStub = {
      loading: signal(false),
      error: signal<string | null>(null),
      snapshot: signal({
        snapshotId: 'snap-1',
        version: 3,
        createdAt: '2026-01-01T00:00:00Z',
      } as never),
      discoveryStatus: signal(null),
      graph: signal(graphFixture()),
      driftOverlay: signal(new Map()),
      selection: signal(null),
      selectionStaleNotice: signal(false),
      connectionStatus: signal('live' as const),
      rackId: signal('rack-1'),
      isLatest: signal(true),
      loadRackTopology,
      selectEntity,
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
          useValue: { connect: signalRConnect, disconnect: () => undefined },
        },
        { provide: TopologyEntityService, useValue: { getEntity: () => new Subject() } },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$.asObservable(),
            queryParamMap: queryParamMap$.asObservable(),
          },
        },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TopologyPageComponent);
  });

  it('loads the rack and connects SignalR for the initial route param', () => {
    paramMap$.next({ get: () => 'rack-1' });
    fixture.detectChanges();

    expect(loadRackTopology).toHaveBeenCalledWith('rack-1');
    expect(signalRConnect).toHaveBeenCalledWith('rack-1');
  });

  it('reacts to a rackId change on the same route (no full reload/component recreation)', () => {
    paramMap$.next({ get: () => 'rack-1' });
    fixture.detectChanges();
    loadRackTopology.mockClear();
    signalRConnect.mockClear();

    paramMap$.next({ get: () => 'rack-2' });

    expect(loadRackTopology).toHaveBeenCalledWith('rack-2');
    expect(signalRConnect).toHaveBeenCalledWith('rack-2');
  });

  it('selecting an edge opens the details panel for its target node (AC3: node OR edge)', () => {
    paramMap$.next({ get: () => 'rack-1' });
    fixture.detectChanges();

    const edge = graphFixture().edges[0];
    (
      fixture.componentInstance as unknown as { onEdgeSelected(e: typeof edge): void }
    ).onEdgeSelected(edge);

    expect(selectEntity).toHaveBeenCalledTimes(1);
    const selected = selectEntity.mock.calls[0][0] as TopologyGraphNode;
    expect(selected.id).toBe('nic:srv-1:eth0');
  });

  it('applies a ?focus= deep link once the graph loads: selects the node and pans/zooms to it (story #171, AC3)', () => {
    const panZoomToNode = vi
      .spyOn(TopologyGraphComponent.prototype, 'panZoomToNode')
      .mockImplementation(() => undefined);

    // Create the graph view first (no ?focus= yet, so the effect returns early).
    paramMap$.next({ get: () => 'rack-1' });
    fixture.detectChanges();
    expect(panZoomToNode).not.toHaveBeenCalled();

    // Now the deep link arrives — the effect selects the focused node and pans/zooms to it.
    queryParamMap$.next(convertToParamMap({ focus: 'nic:srv-1:eth0' }));
    fixture.detectChanges();

    expect(selectEntity).toHaveBeenCalledTimes(1);
    expect((selectEntity.mock.calls[0][0] as TopologyGraphNode).id).toBe('nic:srv-1:eth0');
    expect(panZoomToNode).toHaveBeenCalledWith('nic:srv-1:eth0');
  });
});
