// Automated accessibility check for the VLAN Catalogue screen (NFR5), mirroring
// drift-reports-list.component.a11y.spec.ts (default rendered state) and
// apply-confirmation-dialog.component.a11y.spec.ts (the CDK Dialog overlay, scanned via
// document.body since the overlay renders outside the fixture's own nativeElement). `color-contrast`
// is disabled for the same jsdom-has-no-paint-engine reason as those specs; the real-browser contrast
// pass lives in web/e2e/network-config-harness.spec.ts.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { VlanCatalogueEntryDto } from '../model/network-intent-contracts';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { VlanCatalogueComponent } from './vlan-catalogue.component';

function vlan(overrides: Partial<VlanCatalogueEntryDto> = {}): VlanCatalogueEntryDto {
  return { id: 10, name: 'default', description: null, ...overrides };
}

describe('VlanCatalogueComponent accessibility', () => {
  let fixture: ComponentFixture<VlanCatalogueComponent>;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [VlanCatalogueComponent],
      providers: [
        {
          provide: NetworkIntentStateService,
          useValue: {
            vlanCatalogue: signal([
              vlan({ id: 10, name: 'default' }),
              vlan({ id: 20, name: 'storage', description: 'iSCSI' }),
            ]),
            portIntents: signal([]),
            loading: signal(false),
            loadError: signal(null),
            fieldErrors: signal([]),
          },
        },
        {
          provide: NetworkConfigPermissionService,
          useValue: { canAuthorNetworkConfig: signal(true) },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(VlanCatalogueComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  it('has no automatically-detectable accessibility violations in the default (populated grid) state', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  it('has no automatically-detectable accessibility violations with the Add VLAN dialog open', async () => {
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.vlan-catalogue__add')!.click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.querySelector('.vlan-dialog')).toBeTruthy();

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
