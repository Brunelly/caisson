// Accessibility check for the YAML import dialog (NFR5) — opened via the real @angular/cdk/dialog Dialog
// service (ADR 0034), the same way the shell opens it, so CDK's focus-trap/role=dialog/aria-modal markup
// is included in the scan. color-contrast is disabled (jsdom has no paint engine); the real-browser
// contrast + focus-restoration pass lives in web/e2e/network-config-harness.spec.ts.
import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { YamlImportDialogComponent } from './yaml-import-dialog.component';

describe('YamlImportDialogComponent accessibility', () => {
  afterEach(() => TestBed.inject(Dialog).closeAll());

  it('has no automatically-detectable accessibility violations', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: DesiredStateRoundTripService, useValue: { parse: () => of({ kind: 'ok' }) } },
        {
          provide: NetworkIntentStateService,
          useValue: { applyImportedEnvelope: () => void 0, rackId: signal('rack-1') },
        },
      ],
    });
    const dialog = TestBed.inject(Dialog);

    dialog.open(YamlImportDialogComponent, {
      data: { rackId: 'rack-1' },
      ariaLabelledBy: 'yaml-import-dialog-heading',
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.querySelector('.yaml-import-dialog')).toBeTruthy();

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });
    expect(results.violations).toEqual([]);
  }, 15000);
});
