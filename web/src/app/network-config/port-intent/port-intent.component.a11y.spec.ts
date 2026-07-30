// Automated accessibility check for the Port Intent screen (NFR5), mirroring
// drift-reports-list.component.a11y.spec.ts. `color-contrast` is disabled for the same
// jsdom-has-no-paint-engine reason as that spec; the real-browser contrast pass lives in
// web/e2e/network-config-harness.spec.ts.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import axe from 'axe-core';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { SwitchInventoryDto } from '../../topology/model/topology-contracts';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { NetworkConfigPermissionService } from '../services/network-config-permission.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { PortIntentComponent } from './port-intent.component';

const SWITCH: SwitchInventoryDto = {
  stableKey: 'SW-1',
  serial: 'sw1',
  name: 'SW-1',
  ports: [
    { stableKey: 'SW-1|ether1', portName: 'ether1' },
    { stableKey: 'SW-1|ether2', portName: 'ether2' },
  ],
};

describe('PortIntentComponent accessibility', () => {
  let fixture: ComponentFixture<PortIntentComponent>;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [PortIntentComponent],
      providers: [
        {
          provide: TopologyStateService,
          useValue: {
            switches: signal([SWITCH]),
            loading: signal(false),
            rackId: () => 'rack-1',
            loadRackTopology: () => undefined,
          },
        },
        {
          provide: NetworkIntentStateService,
          useValue: {
            vlanCatalogue: () => [{ id: 20, name: 'storage', description: null }],
            portIntentFor: (switchStableKey: string, portName: string) =>
              switchStableKey === 'SW-1' && portName === 'ether2'
                ? { switchStableKey, portName, accessVlanId: 20 }
                : null,
            setPortIntent: () => undefined,
            clearPortIntent: () => undefined,
          },
        },
        {
          provide: NetworkConfigPermissionService,
          useValue: { canAuthorNetworkConfig: signal(true) },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ rackId: 'rack-1' })),
            snapshot: { queryParamMap: convertToParamMap({}) },
          },
        },
        { provide: Router, useValue: {} },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PortIntentComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
