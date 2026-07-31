// Unit tests for PreflightValidationService (story #170): asserts the outgoing request and the mapped
// ApiResult, mirroring desired-state-roundtrip.service.spec.ts.
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { PreflightValidationResponse } from '../model/preflight-validation-contracts';
import { PreflightValidationService } from './preflight-validation.service';

describe('PreflightValidationService', () => {
  let service: PreflightValidationService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';
  const url = `${environment.apiBaseUrl}/api/racks/${rackId}/desired-state/preflight-validate`;

  const response: PreflightValidationResponse = {
    validationRunId: 'abc',
    isValid: true,
    canCreatePr: true,
    errors: [],
    warnings: [],
    validatedAtUtc: '2026-07-31T00:00:00Z',
    topologySnapshotId: 'snap-1',
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(PreflightValidationService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('POSTs the candidate and maps a 200 to ok', async () => {
    const result = firstValueFrom(
      service.validate(rackId, [{ id: 10, name: 'data', description: null }], []),
    );

    const req = httpMock.expectOne(url);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      vlanCatalogue: [{ id: 10, name: 'data', description: null }],
      portIntents: [],
    });
    req.flush(response);

    await expect(result).resolves.toEqual({ kind: 'ok', value: response });
  });

  it('maps a 403 into the shared forbidden branch', async () => {
    const result = firstValueFrom(service.validate(rackId, [], []));
    httpMock.expectOne(url).flush(null, { status: 403, statusText: 'Forbidden' });
    await expect(result).resolves.toEqual({ kind: 'forbidden' });
  });
});
