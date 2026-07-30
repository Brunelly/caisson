import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DriftItemDto } from '../../drift/model/drift-contracts';
import type { DriftOverlayEntry } from '../../drift/model/drift-topology-overlay';
import type {
  EntityDetailDto,
  EntityDiffDto,
  PortAttachmentDto,
  SnapshotMetadataDto,
} from '../model/topology-contracts';
import type { NicGraphNode, PortGraphNode } from '../model/topology-graph-model';
import { TopologyEntityService } from '../services/topology-entity.service';
import { TopologyStateService } from '../state/topology-state.service';
import type {
  PortAccessIntentDto,
  VlanCatalogueEntryDto,
} from '../../network-config/model/network-intent-contracts';
import { NetworkIntentStateService } from '../../network-config/state/network-intent-state.service';
import { TopologyDetailsPanelComponent } from './topology-details-panel.component';

function attachment(
  portName: string,
  confidence: number,
  switchStableKey = 'SW-1',
  switchSerial: string | null = 'sw1',
): PortAttachmentDto {
  return {
    switchStableKey,
    switchSerial,
    portName,
    confidence,
    band: confidence >= 0.8 ? 'High' : 'Medium',
    reasonCode: 'MultipleMacPorts',
    vlans: [10],
  };
}

function historyEntry(
  changeType: string,
  createdAt: string,
  toSnapshotId: string | null = 'snap-2',
): EntityDiffDto {
  return {
    entityType: 'Nic',
    entityStableKey: 'aabbccddeeff',
    changeType,
    payload: {},
    fromSnapshotId: 'snap-1',
    toSnapshotId,
    createdAt,
    correlationId: 'corr-1',
  };
}

function confirmedNic(): NicGraphNode {
  const best = attachment('ether1', 0.95);
  return {
    id: 'nic:aabbcc',
    type: 'nic',
    stableKey: 'aabbccddeeff',
    mac: 'aa:bb:cc:dd:ee:ff',
    serverId: 'server:srv-1',
    state: 'confirmed',
    unmappedReasonCode: null,
    bestAttachment: best,
    candidates: [best],
    label: 'eth0',
  };
}

function ambiguousNic(): NicGraphNode {
  const best = attachment('ether2', 0.6);
  const other = attachment('ether3', 0.55);
  return {
    ...confirmedNic(),
    id: 'nic:ambiguous',
    state: 'ambiguous',
    bestAttachment: best,
    candidates: [best, other],
  };
}

function unmappedNic(): NicGraphNode {
  return {
    ...confirmedNic(),
    id: 'nic:unmapped',
    state: 'unmapped',
    bestAttachment: null,
    candidates: [],
    unmappedReasonCode: 'NotSeenInSwitch',
  };
}

// A realistic switch port stable key is THREE '|'-separated segments — StableKeys.ForSwitch already
// composes `{deviceKey}|{serial-or-mgmtIp}` for the switch itself, and StableKeys.ForSwitchPort appends
// `|{portName}` on top of that. A flatter two-segment fixture here would mask a truncation bug in
// switchStableKeyFor (it did, in production — see the "network intent drill-down" describe block below).
function portNode(): PortGraphNode {
  return {
    id: 'port:SW-1/ether5',
    type: 'port',
    stableKey: 'SW-1|serial-1|ether5',
    switchId: 'switch:SW-1',
    name: 'ether5',
    state: 'confirmed',
    label: 'ether5',
  };
}

function driftItem(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'drift-item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|SW-1|ether5',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch on SW-1/ether5',
    details: { switchName: 'SW-1', portName: 'ether5' },
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('TopologyDetailsPanelComponent', () => {
  let fixture: ComponentFixture<TopologyDetailsPanelComponent>;
  let selection: ReturnType<typeof signal<NicGraphNode | PortGraphNode | null>>;
  let clearSelection: ReturnType<typeof vi.fn>;
  let getEntity: ReturnType<typeof vi.fn>;

  function setup(
    selectionStaleNotice = false,
    driftOverlay: ReadonlyMap<string, DriftOverlayEntry> = new Map(),
    driftItems: DriftItemDto[] = [],
    portIntents: PortAccessIntentDto[] = [],
    vlanCatalogue: VlanCatalogueEntryDto[] = [],
  ) {
    TestBed.resetTestingModule();
    selection = signal<NicGraphNode | PortGraphNode | null>(null);
    clearSelection = vi.fn(() => selection.set(null));
    getEntity = vi.fn(() =>
      of({
        kind: 'ok',
        value: {
          entityType: 'Nic',
          stableKey: 'k',
          latest: { server: 'srv-01', name: 'eth0', linkState: 'Up' },
          history: [],
        } satisfies EntityDetailDto,
      }),
    );

    const stateStub = {
      selection,
      rackId: signal('rack-1'),
      snapshot: signal({
        snapshotId: 'snap-1',
        version: 5,
        createdAt: '2026-01-01T00:00:00Z',
      } as unknown as SnapshotMetadataDto),
      selectionStaleNotice: signal(selectionStaleNotice),
      driftOverlay: signal(driftOverlay),
      driftItems: signal(driftItems),
      clearSelection,
    };

    // Story #168 AC3: pre-seeded so the constructor effect's `this.networkIntent.load(rackId)` call
    // (guarded by `rackId() !== rackId`) never fires a real HTTP request in this test.
    const networkIntentStub = {
      rackId: signal('rack-1'),
      portIntentFor: (switchStableKey: string, portName: string) =>
        portIntents.find((p) => p.switchStableKey === switchStableKey && p.portName === portName) ??
        null,
      vlanCatalogue: signal(vlanCatalogue),
      load: vi.fn(),
    };

    TestBed.configureTestingModule({
      imports: [TopologyDetailsPanelComponent],
      providers: [
        provideRouter([]),
        { provide: TopologyStateService, useValue: stateStub },
        { provide: TopologyEntityService, useValue: { getEntity } },
        { provide: NetworkIntentStateService, useValue: networkIntentStub },
      ],
    });

    fixture = TestBed.createComponent(TopologyDetailsPanelComponent);
  }

  beforeEach(() => setup());

  it('renders nothing when there is no selection', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.details-panel')).toBeNull();
  });

  it('renders a region (not a modal) with a heading and fetches friendly-labelled latest fields', async () => {
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('.details-panel');
    expect(panel).toBeTruthy();
    expect(panel.getAttribute('role')).toBe('region');
    expect(getEntity).toHaveBeenCalledWith('rack-1', 'Nic', 'aabbccddeeff');

    const text = panel.textContent;
    expect(text).toContain('Server');
    expect(text).toContain('srv-01');
    expect(text).toContain('Link state');
    expect(text).toContain('Up');
  });

  it('shows the snapshot version and id', async () => {
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('v5');
    expect(fixture.nativeElement.textContent).toContain('snap-1');
  });

  it('lists ambiguous candidates with confidence and reason-code copy', async () => {
    selection.set(ambiguousNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('.details-panel__candidates li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('ether2');
    expect(items[0].textContent).toContain('60%');
    expect(items[0].textContent).toContain('more than one candidate port');
  });

  it('shows the unmapped reason instead of a candidate list', async () => {
    selection.set(unmappedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.details-panel__candidates')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('BMC inventory');
  });

  it('shows a non-blocking inline notice when the selected entity is stale, without hiding the panel', async () => {
    setup(true);
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.details-panel')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[role="status"]').textContent).toContain(
      'no longer present',
    );
  });

  it('moves focus to the heading on open', async () => {
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const heading = fixture.nativeElement.querySelector('h2');
    expect(document.activeElement).toBe(heading);
  });

  it('the close button clears the selection', async () => {
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    fixture.nativeElement.querySelector('.details-panel__close').click();
    expect(clearSelection).toHaveBeenCalled();
  });

  it('Escape clears the selection and returns focus to the triggering element', async () => {
    const trigger = document.createElement('button');
    document.body.appendChild(trigger);
    trigger.focus();

    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    fixture.nativeElement
      .querySelector('.details-panel')
      .dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );

    expect(clearSelection).toHaveBeenCalled();
    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });

  it('tracks candidates by switch + port so two same-named ports on different switches never collide', async () => {
    const onSwitchA = attachment('ether1', 0.6, 'SW-A', 'swA');
    const onSwitchB = attachment('ether1', 0.55, 'SW-B', 'swB');
    selection.set({
      ...ambiguousNic(),
      bestAttachment: onSwitchA,
      candidates: [onSwitchA, onSwitchB],
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('.details-panel__candidates li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('swA');
    expect(items[1].textContent).toContain('swB');
  });

  it('applies the DS monospace/tabular-numeral identifier class to identifier fields (PVID/tagged VLANs) but not to non-identifier fields (Up) — Task #129', async () => {
    getEntity.mockReturnValue(
      of({
        kind: 'ok',
        value: {
          entityType: 'SwitchPort',
          stableKey: 'SW-1|ether5',
          latest: { switch: 'SW-1', isUp: 'true', pvid: '10', taggedVlans: '10,20' },
          history: [],
        } satisfies EntityDetailDto,
      }),
    );

    selection.set(portNode());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    function ddFor(label: string): Element | null {
      const dt = Array.from(
        fixture.nativeElement.querySelectorAll('.details-panel__fields dt'),
      ).find((el) => (el as Element).textContent === label) as Element | undefined;
      return dt?.nextElementSibling ?? null;
    }

    expect(ddFor('PVID')?.classList.contains('details-panel__field--identifier')).toBe(true);
    expect(ddFor('Tagged VLANs')?.classList.contains('details-panel__field--identifier')).toBe(
      true,
    );
    expect(ddFor('Up')?.classList.contains('details-panel__field--identifier')).toBe(false);
  });

  it('renders the entity change history returned alongside its latest fields (AC3)', async () => {
    getEntity.mockReturnValue(
      of({
        kind: 'ok',
        value: {
          entityType: 'Nic',
          stableKey: 'k',
          latest: { server: 'srv-01', name: 'eth0', linkState: 'Up' },
          history: [
            historyEntry('Modified', '2026-01-02T00:00:00Z'),
            historyEntry('Added', '2026-01-01T00:00:00Z', null),
          ],
        } satisfies EntityDetailDto,
      }),
    );

    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('.details-panel__history li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Modified');
    expect(items[1].textContent).toContain('Added');
  });

  it('renders no history section for an entity with no recorded changes', async () => {
    selection.set(confirmedNic());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.details-panel__history')).toBeNull();
  });

  it("clears the previous entity's fields immediately on reselection, before the new fetch resolves", async () => {
    const first = new Subject<{ kind: 'ok'; value: EntityDetailDto }>();
    getEntity.mockImplementation(() => first);

    selection.set(confirmedNic());
    fixture.detectChanges();
    first.next({
      kind: 'ok',
      value: {
        entityType: 'Nic',
        stableKey: 'k',
        latest: { server: 'srv-01', name: 'eth0', linkState: 'Up' },
        history: [],
      },
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('srv-01');

    const second = new Subject<{ kind: 'ok'; value: EntityDetailDto }>();
    getEntity.mockImplementation(() => second);
    selection.set({ ...confirmedNic(), id: 'nic:other', stableKey: 'other-key' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('srv-01');

    second.next({
      kind: 'ok',
      value: {
        entityType: 'Nic',
        stableKey: 'other-key',
        latest: { server: 'srv-02', name: 'eth1', linkState: 'Down' },
        history: [],
      },
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('srv-02');
  });

  describe('drift section (story #67)', () => {
    it('renders drift type, severity badge, why, detected date, and a link to the drift report item for a drifted port', async () => {
      const port = portNode();
      const overlay = new Map<string, DriftOverlayEntry>([
        [
          port.id,
          { driftItemId: 'drift-item-1', driftType: 'AccessVlanMismatch', severity: 'High' },
        ],
      ]);
      setup(false, overlay, [driftItem()]);

      selection.set(port);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const section = fixture.nativeElement.querySelector('.details-panel__drift');
      expect(section).toBeTruthy();
      expect(section.textContent).toContain('AccessVlanMismatch');
      expect(section.textContent).toContain('High severity');
      expect(section.textContent).toContain('Access VLAN mismatch on SW-1/ether5');

      const link: HTMLAnchorElement = section.querySelector('.details-panel__link');
      expect(link).toBeTruthy();
      expect(link.getAttribute('href')).toBe('/racks/rack-1/drift/items/drift-item-1');
    });

    it('renders no drift section for a port with no overlay entry', async () => {
      setup(false, new Map(), []);
      selection.set(portNode());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.details-panel__drift')).toBeNull();
    });

    it('renders no drift section for a non-port selection even when the overlay is non-empty', async () => {
      const overlay = new Map<string, DriftOverlayEntry>([
        [
          'port:SW-1/ether5',
          { driftItemId: 'drift-item-1', driftType: 'AccessVlanMismatch', severity: 'High' },
        ],
      ]);
      setup(false, overlay, [driftItem()]);
      selection.set(confirmedNic());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.details-panel__drift')).toBeNull();
    });
  });

  describe('network intent drill-down (story #168, AC3)', () => {
    // portNode()'s stableKey is 'SW-1|serial-1|ether5' — three segments, mirroring a real
    // StableKeys.ForSwitchPort key. The switch's own stable key (StableKeys.ForSwitch) is therefore the
    // FIRST TWO segments, 'SW-1|serial-1', not just 'SW-1' — a truncated switch key would never match an
    // authored intent keyed by the real switch, which is exactly the bug this guards against.
    const fullSwitchStableKey = 'SW-1|serial-1';

    it('renders the authored access-VLAN intent badge, keyed by the FULL (multi-segment) switch stable key', async () => {
      setup(
        false,
        new Map(),
        [],
        [{ switchStableKey: fullSwitchStableKey, portName: 'ether5', accessVlanId: 120 }],
        [{ id: 120, name: 'storage', description: null }],
      );

      selection.set(portNode());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const section = Array.from(
        fixture.nativeElement.querySelectorAll('.details-panel__section'),
      ).find((el) => (el as Element).querySelector('h3')?.textContent === 'Network intent') as
        Element | undefined;
      expect(section).toBeTruthy();
      expect(section!.textContent).toContain('Access VLAN = 120 (storage)');
    });

    it('renders the Unchanged/Inherit badge when no intent is authored for this port', async () => {
      setup(false, new Map(), [], [], []);

      selection.set(portNode());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const section = Array.from(
        fixture.nativeElement.querySelectorAll('.details-panel__section'),
      ).find((el) => (el as Element).querySelector('h3')?.textContent === 'Network intent') as
        Element | undefined;
      expect(section).toBeTruthy();
      expect(section!.textContent).toContain('Unchanged / Inherit');
    });

    it('does NOT render the intent badge when an intent is keyed by only the truncated first segment of the switch key (regression guard)', async () => {
      // A stale/truncated key ('SW-1', dropping the '|serial-1' segment) must never match — this is the
      // exact failure mode the switchStableKeyFor bug produced (it always looked up the truncated key,
      // so a real intent stored against the full key was silently never found).
      setup(
        false,
        new Map(),
        [],
        [{ switchStableKey: 'SW-1', portName: 'ether5', accessVlanId: 120 }],
        [{ id: 120, name: 'storage', description: null }],
      );

      selection.set(portNode());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const section = Array.from(
        fixture.nativeElement.querySelectorAll('.details-panel__section'),
      ).find((el) => (el as Element).querySelector('h3')?.textContent === 'Network intent') as
        Element | undefined;
      expect(section!.textContent).toContain('Unchanged / Inherit');
    });

    it('links "Edit port intent" to the ports route with the FULL switch stable key and port name as query params, for the AC3 deep link', async () => {
      setup(false, new Map(), [], [], []);

      selection.set(portNode());
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const links: HTMLAnchorElement[] = Array.from(
        fixture.nativeElement.querySelectorAll('.details-panel__link'),
      );
      const link = links.find((l) => l.getAttribute('href')?.includes('/network-config/ports'));
      expect(link).toBeTruthy();
      const url = new URL(link!.getAttribute('href')!, 'http://localhost');
      expect(url.pathname).toBe('/racks/rack-1/network-config/ports');
      expect(url.searchParams.get('switch')).toBe(fullSwitchStableKey);
      expect(url.searchParams.get('port')).toBe('ether5');
    });
  });
});
