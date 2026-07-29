import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { SnapshotDetailDto } from '../model/topology-contracts';
import { TopologySnapshotService } from './topology-snapshot.service';

describe('TopologySnapshotService', () => {
  let service: TopologySnapshotService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TopologySnapshotService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('fetches the latest snapshot and wraps it as an ok result', async () => {
    const detail = { snapshot: { snapshotId: 's1' } } as unknown as SnapshotDetailDto;
    const resultPromise = firstValueFrom(service.getLatest(rackId));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/snapshots/latest`,
    );
    expect(req.request.method).toBe('GET');
    req.flush(detail);

    await expect(resultPromise).resolves.toEqual({ kind: 'ok', value: detail });
  });

  it('maps a 403 response to a forbidden result instead of throwing', async () => {
    const resultPromise = firstValueFrom(service.getLatest(rackId));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/snapshots/latest`,
    );
    req.flush('forbidden', { status: 403, statusText: 'Forbidden' });

    await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
  });

  it('maps a 404 response to a notFound result', async () => {
    const resultPromise = firstValueFrom(service.getGraph(rackId));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/snapshots/latest/graph`,
    );
    req.flush('not found', { status: 404, statusText: 'Not Found' });

    await expect(resultPromise).resolves.toEqual({ kind: 'notFound' });
  });

  it('requests a specific snapshot graph by id when snapshotId is passed', async () => {
    const resultPromise = firstValueFrom(service.getGraph(rackId, 'snap-42'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/snapshots/snap-42/graph`,
    );
    req.flush({});

    await resultPromise;
  });

  it('sends from/to query params for the diff endpoint', async () => {
    const resultPromise = firstValueFrom(service.getDiff(rackId, 'snap-1', 'snap-2'));

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/topology/diff`,
    );
    expect(req.request.params.get('from')).toBe('snap-1');
    expect(req.request.params.get('to')).toBe('snap-2');
    req.flush({});

    await resultPromise;
  });

  it('finding #19: percent-encodes a rackId/snapshotId containing reserved URL characters', async () => {
    const trickyRackId = 'rack/1?evil=1#frag';
    const resultPromise = firstValueFrom(service.getById(trickyRackId, 'snap/../../etc'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/topology/snapshots/${encodeURIComponent('snap/../../etc')}`,
    );
    req.flush({});

    await resultPromise;
  });

  it('maps an unexpected status to a generic error result carrying the status code', async () => {
    const resultPromise = firstValueFrom(service.getLatest(rackId));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/snapshots/latest`,
    );
    req.flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(resultPromise).resolves.toEqual({ kind: 'error', status: 500 });
  });
});
