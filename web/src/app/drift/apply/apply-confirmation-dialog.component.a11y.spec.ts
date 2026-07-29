// Automated accessibility check for the Apply confirmation dialog (NFR5) — opened via the real
// @angular/cdk/dialog Dialog service (ADR 0034), the same way apply-action.component.ts opens it, so
// the focus-trap/role=dialog/aria-modal markup CDK provides is included in the scan. `color-contrast`
// is disabled for the same jsdom-has-no-paint-engine reason as topology-page.a11y.spec.ts; the
// real-browser contrast pass lives in web/e2e/drift-harness.spec.ts.
import { Dialog } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { afterEach, describe, expect, it } from 'vitest';
import type { DriftItemDto } from '../model/drift-contracts';
import { ApplyConfirmationDialogComponent } from './apply-confirmation-dialog.component';

function item(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|sw-01|ether5',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch',
    details: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('ApplyConfirmationDialogComponent accessibility', () => {
  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    TestBed.configureTestingModule({});
    const dialog = TestBed.inject(Dialog);

    dialog.open(ApplyConfirmationDialogComponent, {
      data: { item: item() },
      ariaLabelledBy: 'apply-dialog-heading',
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.querySelector('.apply-dialog')).toBeTruthy();

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
