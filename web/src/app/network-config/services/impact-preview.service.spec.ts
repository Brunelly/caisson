// Unit tests for ImpactPreviewService (story #171): asserts the outgoing POST/GET requests and the mapped
// discriminated-union result, mirroring desired-state-roundtrip.service.spec.ts's HttpTestingController
// pattern (expectOne(url), assert method/body, flush(response) / flush(null, { status, statusText })).
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { ImpactPreviewResponse } from '../model/impact-preview-contracts';
import { ImpactPreviewService } from './impact-preview.service';

describe('ImpactPreviewService', () => {
  let service: ImpactPreviewService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';
  const base = `${environment.apiBaseUrl}/api/racks/${rackId}/desired-state`;

  const response: ImpactPreviewResponse = {
    candidateId: 'cand-1',
    candidateSha256: 'sha-candidate',
    baselineSha256: 'sha-baseline',
    baselineRevisionId: 'rev-1',
    baselineCommitSha: 'commit-1',
    cacheHit: false,
    createdAtUtc: '2026-07-31T00:00:00Z',
    rawUnifiedDiff: '@@ -1,1 +1,1 @@\n-old\n+new\n',
    vlanChanges: [],
    portChanges: [],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ImpactPreviewService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('preview POSTs the yaml and maps a 200 to ok', async () => {
    const result = firstValueFrom(service.preview(rackId, 'apiVersion: caisson.dev/v1alpha1\n'));

    const req = httpMock.expectOne(`${base}/impact-preview`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ yaml: 'apiVersion: caisson.dev/v1alpha1\n' });
    req.flush(response);

    await expect(result).resolves.toEqual({ kind: 'ok', value: response });
  });

  it('preview maps a 400 into validationError with the richer issues body', async () => {
    const result = firstValueFrom(service.preview(rackId, 'bad'));

    const req = httpMock.expectOne(`${base}/impact-preview`);
    req.flush(
      { issues: [{ path: 'spec.vlans[0].vlanId', message: 'out of range', line: 7, column: 11 }] },
      { status: 400, statusText: 'Bad Request' },
    );

    await expect(result).resolves.toEqual({
      kind: 'validationError',
      issues: [{ path: 'spec.vlans[0].vlanId', message: 'out of range', line: 7, column: 11 }],
    });
  });

  it('preview maps a 409 into missingBaseline with the reasonCode + message', async () => {
    const result = firstValueFrom(service.preview(rackId, 'x'));

    const req = httpMock.expectOne(`${base}/impact-preview`);
    req.flush(
      {
        reasonCode: 'DESIRED_STATE_BASELINE_MISSING',
        message: 'This rack has no ingested desired-state revision yet.',
      },
      { status: 409, statusText: 'Conflict' },
    );

    await expect(result).resolves.toEqual({
      kind: 'missingBaseline',
      reasonCode: 'DESIRED_STATE_BASELINE_MISSING',
      message: 'This rack has no ingested desired-state revision yet.',
    });
  });

  it('preview maps a 403 into the shared forbidden branch', async () => {
    const result = firstValueFrom(service.preview(rackId, 'x'));
    httpMock
      .expectOne(`${base}/impact-preview`)
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await expect(result).resolves.toEqual({ kind: 'forbidden' });
  });

  it('getByCandidate GETs the candidate impact-preview url and maps a 200 to ok', async () => {
    const result = firstValueFrom(service.getByCandidate(rackId, 'cand-1'));

    const req = httpMock.expectOne(`${base}/candidates/cand-1/impact-preview`);
    expect(req.request.method).toBe('GET');
    req.flush(response);

    await expect(result).resolves.toEqual({ kind: 'ok', value: response });
  });
});
