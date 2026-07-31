// Tests for CreatePrDialogComponent (story #170, AC3): per-warning acknowledgement gating (submit disabled
// until all acknowledged), distinct-by-code rows, and Cancel making no API call (resolves undefined).
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ValidationIssue } from '../model/preflight-validation-contracts';
import {
  CreatePrDialogComponent,
  type CreatePrDialogData,
  type CreatePrDialogResult,
} from './create-pr-dialog.component';

function warning(code: string): ValidationIssue {
  return {
    severity: 'warning',
    code,
    message: `${code} message`,
    fieldPath: '/portIntents/0/accessVlanId',
    uiPath: null,
    entityRef: {
      kind: 'port',
      rackId: 'r',
      switchStableKey: 'sw',
      portName: 'ether2',
      vlanId: null,
    },
    helpUrl: null,
    details: null,
  };
}

describe('CreatePrDialogComponent', () => {
  const closed = vi.fn();
  const dialogRef = { close: closed } as unknown as DialogRef<CreatePrDialogResult>;

  function build(data: CreatePrDialogData) {
    TestBed.configureTestingModule({
      imports: [CreatePrDialogComponent],
      providers: [
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: dialogRef },
      ],
    });
    const fixture = TestBed.createComponent(CreatePrDialogComponent);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    closed.mockReset();
    TestBed.resetTestingModule();
  });

  it('disables submit until every safety warning is acknowledged', () => {
    const fixture = build({
      warnings: [warning('safety.uplinkPort'), warning('safety.managementPort')],
    });
    const host = fixture.nativeElement as HTMLElement;
    const submit = host.querySelector<HTMLButtonElement>('.create-pr-dialog__submit')!;
    const checkboxes = host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');

    expect(checkboxes.length).toBe(2);
    expect(submit.disabled).toBe(true);

    checkboxes[0].checked = true;
    checkboxes[0].dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(submit.disabled).toBe(true);

    checkboxes[1].checked = true;
    checkboxes[1].dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);

    submit.click();
    expect(closed).toHaveBeenCalledWith({
      acknowledgedWarningCodes: ['safety.uplinkPort', 'safety.managementPort'],
    });
  });

  it('deduplicates warnings by code', () => {
    const fixture = build({
      warnings: [warning('safety.uplinkPort'), warning('safety.uplinkPort')],
    });
    expect(fixture.nativeElement.querySelectorAll('input[type="checkbox"]').length).toBe(1);
  });

  it('Cancel resolves with no value and makes no API call', () => {
    const fixture = build({ warnings: [warning('safety.uplinkPort')] });
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector<HTMLButtonElement>('.create-pr-dialog__cancel')!.click();
    expect(closed).toHaveBeenCalledWith(undefined);
  });

  it('has no axe violations', async () => {
    const fixture = build({ warnings: [warning('safety.uplinkPort')] });
    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });
    expect(results.violations).toEqual([]);
  });
});
