// Automated accessibility check for the per-port access-VLAN intent editor dialog (story #168, AC2),
// opened via the real @angular/cdk/dialog Dialog service (ADR 0034) exactly as
// apply-confirmation-dialog.component.a11y.spec.ts does, so the focus-trap/role="dialog"/aria-modal
// markup CDK provides is included in the scan. `color-contrast` is disabled for the same
// jsdom-has-no-paint-engine reason as that spec; the real-browser contrast pass lives in
// web/e2e/network-config-harness.spec.ts.
import { Dialog } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { afterEach, describe, expect, it } from 'vitest';
import type { PortIntentEditorData } from './port-intent-editor.component';
import { PortIntentEditorComponent } from './port-intent-editor.component';

describe('PortIntentEditorComponent accessibility', () => {
  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  it('has no automatically-detectable accessibility violations (Unchanged/Inherit selected)', async () => {
    TestBed.configureTestingModule({});
    const dialog = TestBed.inject(Dialog);

    dialog.open<unknown, PortIntentEditorData>(PortIntentEditorComponent, {
      data: {
        switchStableKey: 'SW-1',
        portName: 'ether1',
        currentVlanId: null,
        catalogue: [
          { id: 10, name: 'default', description: null },
          { id: 20, name: 'storage', description: 'iSCSI' },
        ],
      },
      ariaLabelledBy: 'port-intent-editor-heading',
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.querySelector('.port-intent-editor')).toBeTruthy();

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  it('has no automatically-detectable accessibility violations (a VLAN already selected)', async () => {
    TestBed.configureTestingModule({});
    const dialog = TestBed.inject(Dialog);

    dialog.open<unknown, PortIntentEditorData>(PortIntentEditorComponent, {
      data: {
        switchStableKey: 'SW-1',
        portName: 'ether2',
        currentVlanId: 20,
        catalogue: [
          { id: 10, name: 'default', description: null },
          { id: 20, name: 'storage', description: 'iSCSI' },
        ],
      },
      ariaLabelledBy: 'port-intent-editor-heading',
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
