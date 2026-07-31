// Unit tests for the story-#170 pre-flight state on NetworkIntentStateService: canCreatePr gating (no
// errors + every warning acknowledged + a live validationRunId) and stale-on-edit (any draft mutation
// clears the run so a fresh validation is forced).
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type {
  PreflightValidationResponse,
  ValidationIssue,
} from '../model/preflight-validation-contracts';
import { NetworkIntentStateService } from './network-intent-state.service';

function issue(severity: 'error' | 'warning', code: string): ValidationIssue {
  return {
    severity,
    code,
    message: `${code} message`,
    fieldPath: `/portIntents/0/accessVlanId`,
    uiPath: 'ports["sw/ether2"].accessVlanId',
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

function response(overrides: Partial<PreflightValidationResponse>): PreflightValidationResponse {
  return {
    validationRunId: 'run-1',
    isValid: true,
    canCreatePr: true,
    errors: [],
    warnings: [],
    validatedAtUtc: '2026-07-31T00:00:00Z',
    topologySnapshotId: 'snap-1',
    ...overrides,
  };
}

describe('NetworkIntentStateService pre-flight state', () => {
  let state: NetworkIntentStateService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    state = TestBed.inject(NetworkIntentStateService);
  });

  it('canCreatePr is true for a clean run with no errors or warnings', () => {
    state.applyValidation(response({}));
    expect(state.canCreatePr()).toBe(true);
  });

  it('canCreatePr is false while any error exists', () => {
    state.applyValidation(
      response({ isValid: false, errors: [issue('error', 'schema.vlanIdRange')] }),
    );
    expect(state.canCreatePr()).toBe(false);
  });

  it('canCreatePr is false until every warning code is acknowledged', () => {
    state.applyValidation(response({ warnings: [issue('warning', 'safety.uplinkPort')] }));
    expect(state.canCreatePr()).toBe(false);

    state.acknowledgeWarning('safety.uplinkPort', true);
    expect(state.canCreatePr()).toBe(true);

    state.acknowledgeWarning('safety.uplinkPort', false);
    expect(state.canCreatePr()).toBe(false);
  });

  it('a draft edit clears the validation run, issues and acknowledgements (stale-on-edit)', () => {
    state.applyValidation(response({}));
    expect(state.validationRunId()).toBe('run-1');
    expect(state.canCreatePr()).toBe(true);

    state.addVlan({ id: 20, name: 'new', description: null });

    expect(state.validationRunId()).toBeNull();
    expect(state.issueErrors()).toEqual([]);
    expect(state.issueWarnings()).toEqual([]);
    expect(state.acknowledgedWarningCodes().size).toBe(0);
    expect(state.canCreatePr()).toBe(false);
    expect(state.preflightStatus()).toBe('idle');
  });

  it('setAcknowledgedWarningCodes replaces the acknowledged set wholesale', () => {
    state.applyValidation(response({ warnings: [issue('warning', 'safety.managementPort')] }));
    state.setAcknowledgedWarningCodes(['safety.managementPort']);
    expect(state.canCreatePr()).toBe(true);
  });
});
