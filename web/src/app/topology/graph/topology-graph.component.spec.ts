import { Component, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { TopologyGraphDto } from '../model/topology-contracts';
import { deriveTopologyGraph } from '../model/topology-graph-model';
import { TopologyGraphComponent } from './topology-graph.component';

function fixtureGraph(): TopologyGraphDto {
  return {
    snapshotId: 'snap-1',
    version: 1,
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
            mac: 'aabbccddee01',
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
            mac: 'aabbccddee02',
            bestAttachment: null,
            candidates: [],
            unmappedReasonCode: 'NotSeenInSwitch',
          },
        ],
      },
    ],
    unmappedPorts: [],
  };
}

@Component({
  standalone: true,
  imports: [TopologyGraphComponent],
  template: `<app-topology-graph [graph]="graph" />`,
})
class HostComponent {
  graph = deriveTopologyGraph(fixtureGraph());
  readonly graphComponent = viewChild.required(TopologyGraphComponent);
}

describe('TopologyGraphComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  function svgEl(): SVGSVGElement {
    return fixture.nativeElement.querySelector('svg.topology-graph');
  }

  it('renders one node per server/NIC/switch/port (AC1)', () => {
    const nodes = svgEl().querySelectorAll('g.node');
    // 1 server + 2 nics + 1 switch + 1 port (the unmapped NIC has no port) + 1 vlan = 6
    expect(nodes.length).toBe(6);
  });

  it('renders server-nic and nic-port edges with the correct mapping-state classes', () => {
    const confirmedEdges = svgEl().querySelectorAll('line.edge--confirmed');
    expect(confirmedEdges.length).toBeGreaterThan(0);
  });

  it('gives every node an aria-label describing type and state', () => {
    const nodes = Array.from(svgEl().querySelectorAll('g.node'));
    expect(nodes.every((n) => n.getAttribute('aria-label'))).toBe(true);
    const unmappedNic = nodes.find((n) => n.getAttribute('aria-label')?.includes('eth1'));
    expect(unmappedNic?.getAttribute('aria-label')).toContain('unmapped');
  });

  it('makes every node keyboard-focusable and activatable', () => {
    const node = svgEl().querySelector('g.node') as SVGGElement;
    expect(node.getAttribute('tabindex')).toBe('0');
    expect(node.getAttribute('role')).toBe('button');

    let emitted: unknown;
    fixture.componentInstance.graphComponent().nodeSelected.subscribe((n) => (emitted = n));
    node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(emitted).toBeDefined();
  });

  it('applySnapshot patches existing nodes in place rather than re-mounting the SVG', async () => {
    const firstNodeBefore = svgEl().querySelector('g.node--server') as SVGGElement;
    expect(firstNodeBefore).toBeTruthy();

    const refreshed = deriveTopologyGraph(fixtureGraph());
    fixture.componentInstance.graphComponent().applySnapshot(refreshed);
    await fixture.whenStable();

    const firstNodeAfter = svgEl().querySelector('g.node--server') as SVGGElement;
    // Same DOM element identity survives the patch — proves a keyed D3 join, not a full re-render.
    expect(firstNodeAfter).toBe(firstNodeBefore);
  });

  it('applySnapshot removes nodes no longer present in the new graph (exit join)', async () => {
    const graphComponent = fixture.componentInstance.graphComponent();
    expect(svgEl().querySelectorAll('g.node').length).toBe(6);

    const emptyGraph: TopologyGraphDto = { ...fixtureGraph(), servers: [] };
    graphComponent.applySnapshot(deriveTopologyGraph(emptyGraph));
    await fixture.whenStable();

    expect(svgEl().querySelectorAll('g.node').length).toBe(0);
  });

  it('panZoomToNode does not throw for a known or unknown node id', () => {
    const graphComponent = fixture.componentInstance.graphComponent();
    expect(() => graphComponent.panZoomToNode('server:srv-1')).not.toThrow();
    expect(() => graphComponent.panZoomToNode('does-not-exist')).not.toThrow();
  });
});
