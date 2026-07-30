// Component spec for the Port Intent screen (story #168, AC2/AC3), mirroring
// drift-reports-list.component.spec.ts / vlan-catalogue.component.spec.ts's conventions: signal-backed
// state stubs, a real (not mocked) @angular/cdk/dialog Dialog service so the editor dialog's own
// select-and-Apply flow is exercised for real. `fixture.whenStable()` (not a raw setTimeout) flushes the
// component's `effect()` scheduler, mirroring topology-details-panel.component.spec.ts's convention.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Subject } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  PortAccessIntentDto,
  VlanCatalogueEntryDto,
} from '../model/network-intent-contracts';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import type { SwitchInventoryDto } from '../../topology/model/topology-contracts';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { PortIntentComponent } from './port-intent.component';

function switchInventory(overrides: Partial<SwitchInventoryDto> = {}): SwitchInventoryDto {
  return {
    stableKey: 'SW-1',
    serial: 'sw1',
    name: 'SW-1',
    ports: [
      { stableKey: 'SW-1|ether1', portName: 'ether1' },
      { stableKey: 'SW-1|ether2', portName: 'ether2' },
    ],
    ...overrides,
  };
}

function createNetworkIntentStateStub(
  catalogue: VlanCatalogueEntryDto[] = [],
  portIntents: PortAccessIntentDto[] = [],
) {
  const byKey = new Map<string, PortAccessIntentDto>();
  for (const intent of portIntents) {
    byKey.set(`${intent.switchStableKey}|${intent.portName}`, intent);
  }
  const vlanCatalogue = () => catalogue;
  const setPortIntent = vi.fn((switchStableKey: string, portName: string, accessVlanId: number) => {
    byKey.set(`${switchStableKey}|${portName}`, { switchStableKey, portName, accessVlanId });
  });
  const clearPortIntent = vi.fn((switchStableKey: string, portName: string) => {
    byKey.delete(`${switchStableKey}|${portName}`);
  });
  const portIntentFor = (switchStableKey: string, portName: string) =>
    byKey.get(`${switchStableKey}|${portName}`) ?? null;

  return { vlanCatalogue, setPortIntent, clearPortIntent, portIntentFor };
}

function dialogEl(): HTMLElement | null {
  return document.querySelector('.port-intent-editor');
}

describe('PortIntentComponent', () => {
  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  async function setup(options: {
    switches?: SwitchInventoryDto[];
    canAuthor?: boolean;
    catalogue?: VlanCatalogueEntryDto[];
    portIntents?: PortAccessIntentDto[];
    queryParams?: Record<string, string>;
  }) {
    const switches = options.switches ?? [switchInventory()];
    // Mirrors the real TopologyStateService's timing: `switches` starts empty and only becomes
    // populated once `loadRackTopology` "loads" it (synchronously here, for test simplicity), and
    // `loadRackTopology` sets `_rackId` too — the component's own "is this a rack change?" check
    // (`rackId !== topologyState.rackId()`) depends on that actually converging, or every paramMap
    // emission looks like a fresh rack change and keeps resetting the deep-link auto-open guard.
    const rackIdSignal = signal<string | null>(null);
    const switchesSignal = signal<SwitchInventoryDto[]>([]);
    const loadRackTopology = vi.fn((rackId: string) => {
      rackIdSignal.set(rackId);
      switchesSignal.set(switches);
    });
    const networkIntentState = createNetworkIntentStateStub(
      options.catalogue ?? [],
      options.portIntents ?? [],
    );

    const paramMap$ = new Subject<ReturnType<typeof convertToParamMap>>();

    TestBed.configureTestingModule({
      imports: [PortIntentComponent],
      providers: [
        {
          provide: TopologyStateService,
          useValue: {
            switches: switchesSignal,
            loading: signal(false),
            rackId: rackIdSignal,
            loadRackTopology,
          },
        },
        { provide: NetworkIntentStateService, useValue: networkIntentState },
        {
          provide: NetworkConfigPermissionService,
          useValue: { canAuthorNetworkConfig: signal(options.canAuthor ?? true) },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$.asObservable(),
            snapshot: { queryParamMap: convertToParamMap(options.queryParams ?? {}) },
          },
        },
        { provide: Router, useValue: {} },
      ],
    });

    const fixture = TestBed.createComponent(PortIntentComponent);
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1' }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return { fixture, loadRackTopology, networkIntentState };
  }

  it('shows the explanatory empty-inventory notice and no path to author when there are no switches', async () => {
    const { fixture } = await setup({ switches: [] });

    const empty = fixture.nativeElement.querySelector('.port-intent__empty');
    expect(empty?.textContent).toContain('No switches or ports have been discovered');
    expect(fixture.nativeElement.querySelector('.port-intent__table')).toBeNull();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('renders one row per discovered port with the correct badge (intent-inherit vs intent-access with the resolved VLAN name)', async () => {
    const { fixture } = await setup({
      catalogue: [{ id: 20, name: 'storage', description: null }],
      portIntents: [{ switchStableKey: 'SW-1', portName: 'ether2', accessVlanId: 20 }],
    });

    const rows = fixture.nativeElement.querySelectorAll('.port-intent__table tbody tr');
    expect(rows.length).toBe(2);

    const inheritRow = rows[0];
    expect(inheritRow.querySelector('.status-badge--intent-inherit')).toBeTruthy();

    const accessRow = rows[1];
    const badge = accessRow.querySelector('.status-badge--intent-access');
    expect(badge).toBeTruthy();
    expect(badge.textContent).toContain('20');
    expect(badge.textContent).toContain('storage');
  });

  it('Edit opens the port-intent-editor dialog, and applying a VLAN calls state.setPortIntent', async () => {
    const { fixture, networkIntentState } = await setup({
      catalogue: [{ id: 20, name: 'storage', description: null }],
    });

    const editButton = fixture.nativeElement.querySelector(
      '.port-intent__table tbody tr button',
    ) as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = dialogEl();
    expect(dialog).toBeTruthy();

    const select = dialog!.querySelector<HTMLSelectElement>('#port-intent-editor-select')!;
    select.value = '20';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog!.querySelector<HTMLButtonElement>('.port-intent-editor__apply')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(networkIntentState.setPortIntent).toHaveBeenCalledWith('SW-1', 'ether1', 20);
    expect(dialogEl()).toBeNull();
  });

  it('applying "Unchanged/Inherit" after a VLAN was set calls state.clearPortIntent', async () => {
    const { fixture, networkIntentState } = await setup({
      catalogue: [{ id: 20, name: 'storage', description: null }],
      portIntents: [{ switchStableKey: 'SW-1', portName: 'ether1', accessVlanId: 20 }],
    });

    const editButton = fixture.nativeElement.querySelector(
      '.port-intent__table tbody tr button',
    ) as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = dialogEl()!;
    const select = dialog.querySelector<HTMLSelectElement>('#port-intent-editor-select')!;
    // Pre-filled to the currently-set VLAN.
    expect(select.value).toBe('20');

    select.value = 'inherit';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.port-intent-editor__apply')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(networkIntentState.clearPortIntent).toHaveBeenCalledWith('SW-1', 'ether1');
  });

  it('the ?switch=&port= query param auto-opens the editor exactly once', async () => {
    const { fixture } = await setup({
      queryParams: { switch: 'SW-1', port: 'ether1' },
    });

    expect(dialogEl()).toBeTruthy();
    expect(document.querySelectorAll('.port-intent-editor')).toHaveLength(1);

    // Further, unrelated change-detection passes must never reopen a second overlay.
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.querySelectorAll('.port-intent-editor')).toHaveLength(1);
  });

  it('mutating controls (Edit) are absent without the NetworkConfigAuthor permission', async () => {
    const { fixture } = await setup({ canAuthor: false });

    expect(fixture.nativeElement.querySelector('.port-intent__table button')).toBeNull();
    expect(
      Array.from(fixture.nativeElement.querySelectorAll('th')).some(
        (th) => (th as HTMLElement).textContent === 'Actions',
      ),
    ).toBe(false);
    // The grid itself stays visible/read-only.
    expect(fixture.nativeElement.querySelector('.port-intent__table')).toBeTruthy();
  });
});
