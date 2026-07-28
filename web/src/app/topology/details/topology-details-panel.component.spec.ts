import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  EntityDetailDto,
  EntityDiffDto,
  PortAttachmentDto,
  SnapshotMetadataDto,
} from '../model/topology-contracts';
import type { NicGraphNode } from '../model/topology-graph-model';
import { TopologyEntityService } from '../services/topology-entity.service';
import { TopologyStateService } from '../state/topology-state.service';
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

describe('TopologyDetailsPanelComponent', () => {
  let fixture: ComponentFixture<TopologyDetailsPanelComponent>;
  let selection: ReturnType<typeof signal<NicGraphNode | null>>;
  let clearSelection: ReturnType<typeof vi.fn>;
  let getEntity: ReturnType<typeof vi.fn>;

  function setup(selectionStaleNotice = false) {
    TestBed.resetTestingModule();
    selection = signal<NicGraphNode | null>(null);
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
      clearSelection,
    };

    TestBed.configureTestingModule({
      imports: [TopologyDetailsPanelComponent],
      providers: [
        { provide: TopologyStateService, useValue: stateStub },
        { provide: TopologyEntityService, useValue: { getEntity } },
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
});
