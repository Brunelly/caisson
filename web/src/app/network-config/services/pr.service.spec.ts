// Unit tests for PrService (story #170): the 202 success mapping and the 422 gateRejected branch that
// carries the reasonCode + the re-validated issue set back to the UI.
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type {
  CreatePrResponse,
  PreflightValidationResponse,
} from '../model/preflight-validation-contracts';
import { PrService } from './pr.service';

describe('PrService', () => {
  let service: PrService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';
  const url = `${environment.apiBaseUrl}/api/racks/${rackId}/desired-state/prs`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(PrService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('POSTs the run id + acknowledged codes and maps a 202 to ok', async () => {
    const body: CreatePrResponse = {
      validationRunId: 'run-1',
      status: 'gate-passed',
      detail: 'ok',
      pullRequestUrl: null,
    };
    const result = firstValueFrom(
      service.createPullRequest(rackId, 'run-1', ['safety.uplinkPort'], [], []),
    );

    const req = httpMock.expectOne(url);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toMatchObject({
      validationRunId: 'run-1',
      acknowledgedWarningCodes: ['safety.uplinkPort'],
    });
    req.flush(body, { status: 202, statusText: 'Accepted' });

    await expect(result).resolves.toEqual({ kind: 'ok', value: body });
  });

  it('maps a 422 into gateRejected with the reasonCode and re-validated issues', async () => {
    const issues: PreflightValidationResponse = {
      validationRunId: 'run-2',
      isValid: false,
      canCreatePr: false,
      errors: [],
      warnings: [],
      validatedAtUtc: '2026-07-31T00:00:00Z',
      topologySnapshotId: null,
    };
    const result = firstValueFrom(service.createPullRequest(rackId, 'stale', [], [], []));

    httpMock
      .expectOne(url)
      .flush(
        { reasonCode: 'revalidate', issues },
        { status: 422, statusText: 'Unprocessable Entity' },
      );

    await expect(result).resolves.toEqual({
      kind: 'gateRejected',
      reasonCode: 'revalidate',
      response: issues,
    });
  });
});
