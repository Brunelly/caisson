// Accessibility + behaviour tests for ValidationIssuesPanel (story #170, NFR6): grouped errors/warnings/
// safety with dot+text severity (colour never the sole indicator), assertive/polite live regions, focus
// moving to the first error after validation, keyboard-operable issue rows, and no axe violations.
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type {
  PreflightValidationResponse,
  ValidationIssue,
} from '../model/preflight-validation-contracts';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { ValidationIssuesPanelComponent } from './validation-issues-panel.component';

function issue(severity: 'error' | 'warning', code: string, fieldPath: string): ValidationIssue {
  return {
    severity,
    code,
    message: `${code} is a problem`,
    fieldPath,
    uiPath: null,
    entityRef: { kind: 'vlan', rackId: 'r', switchStableKey: null, portName: null, vlanId: 2 },
    helpUrl: null,
    details: null,
  };
}

function response(overrides: Partial<PreflightValidationResponse>): PreflightValidationResponse {
  return {
    validationRunId: 'run-1',
    isValid: false,
    canCreatePr: false,
    errors: [],
    warnings: [],
    validatedAtUtc: '2026-07-31T00:00:00Z',
    topologySnapshotId: null,
    ...overrides,
  };
}

describe('ValidationIssuesPanelComponent accessibility', () => {
  let state: NetworkIntentStateService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ValidationIssuesPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    state = TestBed.inject(NetworkIntentStateService);
  });

  afterEach(() => {
    document.body.querySelectorAll('.panel').forEach((n) => n.remove());
  });

  it('renders errors in an assertive live region and warnings/safety politely, with dot + text severity', async () => {
    state.applyValidation(
      response({
        errors: [issue('error', 'schema.vlanIdRange', '/vlanCatalogue/2/id')],
        warnings: [
          issue('warning', 'safety.uplinkPort', '/portIntents/0/accessVlanId'),
          issue('warning', 'other.warning', '/portIntents/1/accessVlanId'),
        ],
      }),
    );

    const fixture = TestBed.createComponent(ValidationIssuesPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    const errorGroup = host.querySelector('.group--errors')!;
    expect(errorGroup.getAttribute('role')).toBe('alert');
    expect(errorGroup.getAttribute('aria-live')).toBe('assertive');

    const safetyGroup = host.querySelector('.group--safety')!;
    expect(safetyGroup.getAttribute('aria-live')).toBe('polite');

    // Colour is never the sole indicator: each row carries a textual severity label.
    const firstError = host.querySelector('.issue[data-group="errors"]')!;
    expect(firstError.querySelector('.issue__severity')!.textContent).toContain('Error');
    expect(firstError.querySelector('.issue__path')!.textContent).toContain('/vlanCatalogue/2/id');

    const results = await axe.run(host, { rules: { 'color-contrast': { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it('moves focus to the first error after a validation with errors', async () => {
    const fixture = TestBed.createComponent(ValidationIssuesPanelComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    state.applyValidation(
      response({ errors: [issue('error', 'schema.vlanIdRange', '/vlanCatalogue/0/id')] }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();

    expect(document.activeElement).toBe(
      fixture.nativeElement.querySelector('.issue[data-group="errors"]'),
    );
  });

  it('renders issue rows as keyboard-operable buttons', async () => {
    state.applyValidation(
      response({ errors: [issue('error', 'schema.vlanIdRange', '/vlanCatalogue/0/id')] }),
    );
    const fixture = TestBed.createComponent(ValidationIssuesPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const row = fixture.nativeElement.querySelector('.issue');
    expect(row.tagName).toBe('BUTTON');
    expect(row.getAttribute('type')).toBe('button');
  });

  it('shows shimmer skeletons in a polite live region while validating', async () => {
    state.beginValidation();
    const fixture = TestBed.createComponent(ValidationIssuesPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const loading = fixture.nativeElement.querySelector('.panel__loading');
    expect(loading.getAttribute('aria-live')).toBe('polite');
    expect(fixture.nativeElement.querySelectorAll('.panel__skeleton').length).toBe(3);
  });
});
