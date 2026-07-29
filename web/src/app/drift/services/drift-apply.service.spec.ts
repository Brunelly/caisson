import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import { DriftApplyService } from './drift-apply.service';

describe('DriftApplyService', () => {
  let service: DriftApplyService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(DriftApplyService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('posts driftItemId and maps a 201 response to a created result', async () => {
    const resultPromise = firstValueFrom(service.applyCorrection(rackId, 'item-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/apply`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ driftItemId: 'item-1' });
    req.flush({ jobId: 'job-1' }, { status: 201, statusText: 'Created' });

    await expect(resultPromise).resolves.toEqual({ kind: 'created', jobId: 'job-1' });
  });

  it('maps a 202 response (existing active job) to an existingJob result', async () => {
    const resultPromise = firstValueFrom(service.applyCorrection(rackId, 'item-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/apply`);
    req.flush({ jobId: 'job-existing' }, { status: 202, statusText: 'Accepted' });

    await expect(resultPromise).resolves.toEqual({ kind: 'existingJob', jobId: 'job-existing' });
  });

  it('maps a 422 response to an unprocessable result carrying the reasonCode', async () => {
    const resultPromise = firstValueFrom(service.applyCorrection(rackId, 'item-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/apply`);
    req.flush(
      { reasonCode: 'unsupported-drift-type' },
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    await expect(resultPromise).resolves.toEqual({
      kind: 'unprocessable',
      reasonCode: 'unsupported-drift-type',
    });
  });

  it('maps a 429 response to a rateLimited result', async () => {
    const resultPromise = firstValueFrom(service.applyCorrection(rackId, 'item-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/apply`);
    req.flush('too many requests', { status: 429, statusText: 'Too Many Requests' });

    await expect(resultPromise).resolves.toEqual({ kind: 'rateLimited' });
  });

  it('maps a 403 response to a forbidden result', async () => {
    const resultPromise = firstValueFrom(service.applyCorrection(rackId, 'item-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/apply`);
    req.flush('forbidden', { status: 403, statusText: 'Forbidden' });

    await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
  });

  it('fetches a job by id', async () => {
    const resultPromise = firstValueFrom(service.getJob(rackId, 'job-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/jobs/job-1`);
    expect(req.request.method).toBe('GET');
    req.flush({ jobId: 'job-1' });

    await expect(resultPromise).resolves.toEqual({ kind: 'ok', value: { jobId: 'job-1' } });
  });

  it('sends state/cursor/pageSize query params for getJobs', async () => {
    const resultPromise = firstValueFrom(
      service.getJobs(rackId, { state: 'Executing', cursor: 'c1', pageSize: 5 }),
    );

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/jobs`,
    );
    expect(req.request.params.get('state')).toBe('Executing');
    expect(req.request.params.get('cursor')).toBe('c1');
    expect(req.request.params.get('pageSize')).toBe('5');
    req.flush({});

    await resultPromise;
  });

  it('finding #19: percent-encodes rackId/jobId containing reserved URL characters', async () => {
    const trickyRackId = 'rack/1?evil=1#frag';
    const trickyJobId = 'job/../../etc';

    const jobPromise = firstValueFrom(service.getJob(trickyRackId, trickyJobId));
    httpMock
      .expectOne(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/jobs/${encodeURIComponent(trickyJobId)}`,
      )
      .flush({});
    await jobPromise;

    const applyPromise = firstValueFrom(service.applyCorrection(trickyRackId, 'item-1'));
    httpMock
      .expectOne(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/drift/apply`,
      )
      .flush({ jobId: 'job-1' }, { status: 201, statusText: 'Created' });
    await applyPromise;
  });
});
