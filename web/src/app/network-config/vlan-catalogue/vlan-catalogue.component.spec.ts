// Component spec for the VLAN Catalogue screen (story #168, AC1), structurally mirroring
// drift-reports-list.component.spec.ts: a signal-backed state-service stub (real @angular/core
// `signal()`s so mutating methods can realistically update what the template reads back) plus the
// REAL @angular/cdk/dialog Dialog service (not mocked) so Add/Edit dialog interactions exercise real
// markup/validation, mirroring apply-action.component.spec.ts's approach to CDK Dialog.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { NetworkIntentFieldError } from '../services/network-intent.service';
import type { PortAccessIntentDto, VlanCatalogueEntryDto } from '../model/network-intent-contracts';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { VlanCatalogueComponent } from './vlan-catalogue.component';

function vlan(overrides: Partial<VlanCatalogueEntryDto> = {}): VlanCatalogueEntryDto {
  return { id: 10, name: 'default', description: null, ...overrides };
}

function createStateStub(
  initialVlans: VlanCatalogueEntryDto[] = [],
  initialPortIntents: PortAccessIntentDto[] = [],
) {
  const vlanCatalogue = signal<VlanCatalogueEntryDto[]>(initialVlans);
  const portIntents = signal<PortAccessIntentDto[]>(initialPortIntents);
  const loading = signal(false);
  const loadError = signal<string | null>(null);
  const fieldErrors = signal<NetworkIntentFieldError[]>([]);

  const addVlan = vi.fn((entry: VlanCatalogueEntryDto) =>
    vlanCatalogue.update((list) => [...list, entry]),
  );
  const updateVlan = vi.fn((id: number, entry: VlanCatalogueEntryDto) =>
    vlanCatalogue.update((list) => list.map((v) => (v.id === id ? entry : v))),
  );
  const retireVlan = vi.fn((id: number) =>
    vlanCatalogue.update((list) => list.filter((v) => v.id !== id)),
  );

  const focusTarget = signal<unknown>(null);
  const clearFocusTarget = vi.fn(() => focusTarget.set(null));

  return {
    vlanCatalogue,
    portIntents,
    loading,
    loadError,
    fieldErrors,
    addVlan,
    updateVlan,
    retireVlan,
    focusTarget,
    clearFocusTarget,
  };
}

type StateStub = ReturnType<typeof createStateStub>;

async function setup(state: StateStub, canAuthor: boolean) {
  TestBed.configureTestingModule({
    imports: [VlanCatalogueComponent],
    providers: [
      { provide: NetworkIntentStateService, useValue: state },
      {
        provide: NetworkConfigPermissionService,
        useValue: { canAuthorNetworkConfig: signal(canAuthor) },
      },
    ],
  });

  const fixture = TestBed.createComponent(VlanCatalogueComponent);
  fixture.detectChanges();
  return fixture;
}

function fillAndSubmit(
  dialogEl: HTMLElement,
  fixture: ComponentFixture<unknown>,
  values: { id?: number; name?: string; description?: string },
): void {
  if (values.id !== undefined) {
    const idInput = dialogEl.querySelector<HTMLInputElement>('#vlan-dialog-id')!;
    idInput.value = String(values.id);
    idInput.dispatchEvent(new Event('input'));
  }
  if (values.name !== undefined) {
    const nameInput = dialogEl.querySelector<HTMLInputElement>('#vlan-dialog-name')!;
    nameInput.value = values.name;
    nameInput.dispatchEvent(new Event('input'));
  }
  if (values.description !== undefined) {
    const descInput = dialogEl.querySelector<HTMLInputElement>('#vlan-dialog-description')!;
    descInput.value = values.description;
    descInput.dispatchEvent(new Event('input'));
  }
  fixture.detectChanges();
  dialogEl.querySelector<HTMLButtonElement>('.vlan-dialog__submit')!.click();
  fixture.detectChanges();
}

function dialogEl(): HTMLElement | null {
  return document.querySelector('.vlan-dialog');
}

describe('VlanCatalogueComponent', () => {
  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  it('renders the empty state when there are no VLANs', async () => {
    const fixture = await setup(createStateStub([]), true);

    const status = fixture.nativeElement.querySelector('[role="status"]');
    expect(status?.textContent).toContain('No VLANs defined for this rack yet.');
    expect(fixture.nativeElement.querySelector('.vlan-catalogue__table')).toBeNull();
  });

  it('renders a list of VLANs, one row per entry', async () => {
    const fixture = await setup(
      createStateStub([vlan({ id: 10, name: 'default' }), vlan({ id: 20, name: 'storage' })]),
      true,
    );

    const rows = fixture.nativeElement.querySelectorAll('.vlan-catalogue__table tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('10');
    expect(rows[0].textContent).toContain('default');
    expect(rows[1].textContent).toContain('20');
    expect(rows[1].textContent).toContain('storage');
  });

  it('Add VLAN opens the dialog, and adding a valid entry calls state.addVlan and updates the rendered list', async () => {
    const state = createStateStub([vlan({ id: 10, name: 'default' })]);
    const fixture = await setup(state, true);

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.vlan-catalogue__add')!
      .click();
    fixture.detectChanges();

    const dialog = dialogEl();
    expect(dialog).toBeTruthy();

    fillAndSubmit(dialog!, fixture, { id: 20, name: 'storage', description: 'iSCSI' });

    expect(state.addVlan).toHaveBeenCalledWith({ id: 20, name: 'storage', description: 'iSCSI' });
    expect(dialogEl()).toBeNull();
    expect(state.vlanCatalogue()).toEqual([
      vlan({ id: 10, name: 'default' }),
      { id: 20, name: 'storage', description: 'iSCSI' },
    ]);
    const rows = fixture.nativeElement.querySelectorAll('.vlan-catalogue__table tbody tr');
    expect(rows.length).toBe(2);
  });

  it('rejects a duplicate VLAN ID inline in the dialog — the dialog stays open and addVlan is never called', async () => {
    const state = createStateStub([vlan({ id: 10, name: 'default' })]);
    const fixture = await setup(state, true);

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.vlan-catalogue__add')!
      .click();
    fixture.detectChanges();
    const dialog = dialogEl()!;

    fillAndSubmit(dialog, fixture, { id: 10, name: 'duplicate' });

    expect(state.addVlan).not.toHaveBeenCalled();
    expect(dialogEl()).toBeTruthy();
    const error = dialog.querySelector('#vlan-dialog-id-error');
    expect(error?.textContent).toContain('VLAN ID 10 already exists in this rack.');
  });

  it('Edit updates an existing entry via state.updateVlan and the rendered row reflects the change', async () => {
    const state = createStateStub([vlan({ id: 10, name: 'default', description: null })]);
    const fixture = await setup(state, true);

    const editButton = fixture.nativeElement.querySelectorAll(
      '.vlan-catalogue__row-actions button',
    )[0] as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();

    const dialog = dialogEl()!;
    // VLAN id is read-only when editing.
    expect(dialog.querySelector<HTMLInputElement>('#vlan-dialog-id')!.readOnly).toBe(true);

    fillAndSubmit(dialog, fixture, { name: 'renamed' });

    expect(state.updateVlan).toHaveBeenCalledWith(10, {
      id: 10,
      name: 'renamed',
      description: null,
    });
    const row = fixture.nativeElement.querySelector('.vlan-catalogue__table tbody tr');
    expect(row.textContent).toContain('renamed');
  });

  it('Retire removes an unreferenced VLAN', async () => {
    const state = createStateStub(
      [vlan({ id: 10, name: 'default' }), vlan({ id: 20, name: 'storage' })],
      [],
    );
    const fixture = await setup(state, true);

    const retireButtons = fixture.nativeElement.querySelectorAll(
      '.vlan-catalogue__row-actions button',
    );
    // Second button in the first row's actions is Retire.
    (retireButtons[1] as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(state.retireVlan).toHaveBeenCalledWith(10);
    expect(state.vlanCatalogue()).toEqual([vlan({ id: 20, name: 'storage' })]);
  });

  it('Retire is BLOCKED for a VLAN referenced by a port intent: inline error shown, no state mutation', async () => {
    const state = createStateStub(
      [vlan({ id: 10, name: 'default' }), vlan({ id: 20, name: 'storage' })],
      [{ switchStableKey: 'SW-1', portName: 'ether2', accessVlanId: 20 }],
    );
    const fixture = await setup(state, true);

    const rows = fixture.nativeElement.querySelectorAll('.vlan-catalogue__table tbody tr');
    const secondRowRetire = rows[1].querySelectorAll(
      '.vlan-catalogue__row-actions button',
    )[1] as HTMLButtonElement;
    secondRowRetire.click();
    fixture.detectChanges();

    expect(state.retireVlan).not.toHaveBeenCalled();
    expect(state.vlanCatalogue()).toHaveLength(2);
    const blocked = fixture.nativeElement.querySelector('.vlan-catalogue__retire-blocked');
    expect(blocked).toBeTruthy();
    expect(blocked.textContent).toContain('still referenced by a port intent');
  });

  it('mutating controls (Add/Edit/Retire) are present when canAuthorNetworkConfig() is true', async () => {
    const fixture = await setup(createStateStub([vlan()]), true);

    expect(fixture.nativeElement.querySelector('.vlan-catalogue__add')).toBeTruthy();
    expect(
      fixture.nativeElement.querySelectorAll('.vlan-catalogue__row-actions button'),
    ).toHaveLength(2);
    expect(
      Array.from(fixture.nativeElement.querySelectorAll('th')).some(
        (th) => (th as HTMLElement).textContent === 'Actions',
      ),
    ).toBe(true);
  });

  it('mutating controls (Add/Edit/Retire) are entirely ABSENT from the DOM when canAuthorNetworkConfig() is false', async () => {
    const fixture = await setup(createStateStub([vlan()]), false);

    expect(fixture.nativeElement.querySelector('.vlan-catalogue__add')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.vlan-catalogue__row-actions')).toHaveLength(0);
    expect(
      Array.from(fixture.nativeElement.querySelectorAll('th')).some(
        (th) => (th as HTMLElement).textContent === 'Actions',
      ),
    ).toBe(false);
    // The grid itself stays visible/read-only for a Read Only user.
    expect(fixture.nativeElement.querySelector('.vlan-catalogue__table')).toBeTruthy();
  });
});
