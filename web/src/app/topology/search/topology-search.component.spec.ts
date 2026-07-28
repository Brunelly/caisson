import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { TopologyGraphDto } from '../model/topology-contracts';
import { deriveTopologyGraph } from '../model/topology-graph-model';
import { TopologyStateService } from '../state/topology-state.service';
import { TopologySearchComponent } from './topology-search.component';

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
            mac: 'aa:bb:cc:dd:ee:01',
            bestAttachment: {
              switchStableKey: 'SW-1',
              switchSerial: 'sw1',
              portName: 'ether24',
              confidence: 0.95,
              band: 'High',
              reasonCode: 'MacLearnUnique',
              vlans: [120],
            },
            candidates: [
              {
                switchStableKey: 'SW-1',
                switchSerial: 'sw1',
                portName: 'ether24',
                confidence: 0.95,
                band: 'High',
                reasonCode: 'MacLearnUnique',
                vlans: [120],
              },
            ],
            unmappedReasonCode: null,
          },
        ],
      },
    ],
    unmappedPorts: [],
  };
}

describe('TopologySearchComponent', () => {
  let fixture: ComponentFixture<TopologySearchComponent>;
  let component: TopologySearchComponent;

  beforeEach(async () => {
    const stateStub = { graph: signal(deriveTopologyGraph(fixtureGraph())) };

    await TestBed.configureTestingModule({
      imports: [TopologySearchComponent],
      providers: [{ provide: TopologyStateService, useValue: stateStub }],
    }).compileComponents();

    fixture = TestBed.createComponent(TopologySearchComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
  });

  function input(): HTMLInputElement {
    return fixture.nativeElement.querySelector('input');
  }

  function type(query: string): void {
    // Sets both the DOM value and the model directly (bypassing ngModel's own change detection
    // cycle) since ngModel's (input) listener would otherwise read the native element's untouched
    // value and stomp queryModel back to empty.
    input().value = query;
    component.queryModel = query;
    component['onInput']();
    fixture.detectChanges();
  }

  function listbox(): HTMLElement | null {
    return document.querySelector('ul.topology-search__results');
  }

  it('has full ARIA combobox wiring on the input', () => {
    const el = input();
    expect(el.getAttribute('role')).toBe('combobox');
    expect(el.getAttribute('aria-haspopup')).toBe('listbox');
    expect(el.getAttribute('aria-autocomplete')).toBe('list');
    expect(el.getAttribute('aria-label')).toBeTruthy();
  });

  it('is closed with no query', () => {
    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(listbox()).toBeNull();
  });

  it('opens and groups matches by entity type on a hostname query', async () => {
    type('srv-01');
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();

    expect(input().getAttribute('aria-expanded')).toBe('true');
    const panel = listbox();
    expect(panel).toBeTruthy();
    expect(panel!.getAttribute('role')).toBe('listbox');
    const groups = panel!.querySelectorAll('li[role="group"]');
    expect(groups.length).toBeGreaterThan(0);
  });

  it.each([['aabbccddee01'], ['aa:bb:cc:dd:ee:01'], ['AA-BB-CC-DD-EE-01']])(
    'matches a NIC MAC regardless of separator style: %s',
    async (query) => {
      type(query);
      await new Promise((r) => setTimeout(r, 200));
      fixture.detectChanges();

      const options = listbox()!.querySelectorAll('li[role="option"]');
      expect(options.length).toBe(1);
      expect(options[0].textContent).toContain('eth0');
    },
  );

  it('matches "vlan 120" against a "VLAN 120" label despite the space', async () => {
    type('vlan 120');
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();

    const options = listbox()!.querySelectorAll('li[role="option"]');
    expect(Array.from(options).some((o) => o.textContent?.includes('VLAN 120'))).toBe(true);
  });

  it('ArrowDown/ArrowUp move aria-activedescendant and wrap around', async () => {
    type('e'); // matches multiple entries (server, nic, port, ...)
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();

    const first = input().getAttribute('aria-activedescendant');
    input().dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    const second = input().getAttribute('aria-activedescendant');
    expect(second).not.toBe(first);

    input().dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(input().getAttribute('aria-activedescendant')).toBe(first);
  });

  it('Enter selects the active option, emits resultSelected, and closes the panel', async () => {
    let emitted: unknown;
    component.resultSelected.subscribe((n) => (emitted = n));

    type('srv-01');
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();

    input().dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(emitted).toBeDefined();
    expect((emitted as { type: string }).type).toBe('server');
    expect(input().getAttribute('aria-expanded')).toBe('false');
  });

  it('Escape closes the panel and returns focus to the input', async () => {
    type('srv-01');
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();
    expect(listbox()).toBeTruthy();

    input().dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(input());
  });

  it('does not treat Space as a selection key, so a space can be typed mid-query', async () => {
    type('vlan');
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();
    const before = input().getAttribute('aria-expanded');

    const spaceEvent = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });
    input().dispatchEvent(spaceEvent);

    expect(spaceEvent.defaultPrevented).toBe(false);
    expect(input().getAttribute('aria-expanded')).toBe(before);
  });
});
