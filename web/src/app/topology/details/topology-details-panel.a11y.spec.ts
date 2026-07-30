// Automated accessibility check for the details panel (Story #123 Task #143), mirroring
// topology-page.a11y.spec.ts's jsdom axe pattern. `color-contrast` is disabled here — jsdom has no real
// layout/paint engine, so it cannot compute rendered colours; the real, meaningful contrast check is the
// Playwright e2e pass (topology-harness.spec.ts), which runs axe with color-contrast enabled in a real
// browser. This spec exists mainly to cover the panel's markup on its own (rather than only ever as part
// of the whole topology-page.a11y.spec.ts fixture) now that it also renders a mobile-only scrim sibling
// element (`.details-panel__scrim`, topology-details-panel.component.scss) alongside the panel itself.
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import axe from 'axe-core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { NicGraphNode } from '../model/topology-graph-model';
import { TopologyEntityService } from '../services/topology-entity.service';
import { TopologyStateService } from '../state/topology-state.service';
import { TopologyDetailsPanelComponent } from './topology-details-panel.component';

function ambiguousNic(): NicGraphNode {
  return {
    id: 'nic:ambiguous',
    type: 'nic',
    stableKey: 'aabbccddeeff',
    mac: 'aa:bb:cc:dd:ee:ff',
    serverId: 'server:srv-1',
    state: 'ambiguous',
    unmappedReasonCode: null,
    bestAttachment: {
      switchStableKey: 'SW-1',
      switchSerial: 'sw1',
      portName: 'ether2',
      confidence: 0.6,
      band: 'Medium',
      reasonCode: 'MultipleMacPorts',
      vlans: [10],
    },
    candidates: [
      {
        switchStableKey: 'SW-1',
        switchSerial: 'sw1',
        portName: 'ether2',
        confidence: 0.6,
        band: 'Medium',
        reasonCode: 'MultipleMacPorts',
        vlans: [10],
      },
    ],
    label: 'eth1',
  };
}

describe('TopologyDetailsPanelComponent accessibility', () => {
  let fixture: ComponentFixture<TopologyDetailsPanelComponent>;

  beforeEach(async () => {
    const stateStub = {
      selection: signal<NicGraphNode | null>(ambiguousNic()),
      rackId: signal('rack-1'),
      snapshot: signal({
        snapshotId: 'snap-1',
        version: 5,
        createdAt: '2026-01-01T00:00:00Z',
      } as never),
      selectionStaleNotice: signal(false),
      driftOverlay: signal(new Map()),
      driftItems: signal([]),
      clearSelection: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [TopologyDetailsPanelComponent],
      providers: [
        provideRouter([]),
        { provide: TopologyStateService, useValue: stateStub },
        {
          provide: TopologyEntityService,
          useValue: {
            getEntity: () =>
              of({
                kind: 'ok',
                value: { entityType: 'Nic', stableKey: 'k', latest: {}, history: [] },
              }),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TopologyDetailsPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has no automatically-detectable accessibility violations, including the mobile-only scrim sibling', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: {
        'color-contrast': { enabled: false },
      },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  it('renders the mobile-only scrim as a decorative, non-modal sibling (never a second interactive surface without a role)', () => {
    const scrim = fixture.nativeElement.querySelector('.details-panel__scrim');
    expect(scrim).toBeTruthy();
    expect(scrim.getAttribute('aria-hidden')).toBe('true');

    // Non-modal, per topology-details-panel.component.ts's own header comment: role="region", not
    // role="dialog"/"alertdialog", and no aria-modal anywhere in the panel.
    const panel = fixture.nativeElement.querySelector('.details-panel');
    expect(panel.getAttribute('role')).toBe('region');
    expect(panel.getAttribute('aria-modal')).toBeNull();
  });
});
