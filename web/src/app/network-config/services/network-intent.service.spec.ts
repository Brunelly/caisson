// HttpTestingController-based coverage for NetworkIntentService (story #168/#176), mirroring
// drift-apply.service.spec.ts's pattern: assert method/URL/body/headers per call, flush a canned
// response, and assert the mapped ApiResult/NetworkIntentSaveResult branch.
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { NetworkIntentDto, NetworkIntentSaveRequest } from '../model/network-intent-contracts';
import { NetworkIntentService } from './network-intent.service';

describe('NetworkIntentService', () => {
  let service: NetworkIntentService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';
  const url = `${environment.apiBaseUrl}/api/racks/${rackId}/network-intent`;

  const dto: NetworkIntentDto = {
    rackId,
    vlanCatalogue: [{ id: 10, name: 'default', description: null }],
    portIntents: [],
    updatedAtUtc: '2026-01-01T00:00:00Z',
    updatedBy: 'someone',
  };

  const request: NetworkIntentSaveRequest = {
    vlanCatalogue: dto.vlanCatalogue,
    portIntents: dto.portIntents,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NetworkIntentService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  describe('getIntent', () => {
    it('maps a 200 response to ok, reading the ETag response header', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));

      const req = httpMock.expectOne(url);
      expect(req.request.method).toBe('GET');
      req.flush(dto, { status: 200, statusText: 'OK', headers: { ETag: 'etag-1' } });

      await expect(resultPromise).resolves.toEqual({
        kind: 'ok',
        value: { intent: dto, etag: 'etag-1' },
      });
    });

    it('maps a 200 response with no ETag header to a null etag', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));

      httpMock.expectOne(url).flush(dto, { status: 200, statusText: 'OK' });

      await expect(resultPromise).resolves.toEqual({ kind: 'ok', value: { intent: dto, etag: null } });
    });

    it('maps 401 to unauthorized', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));
      httpMock.expectOne(url).flush('unauthorized', { status: 401, statusText: 'Unauthorized' });
      await expect(resultPromise).resolves.toEqual({ kind: 'unauthorized' });
    });

    it('maps 403 to forbidden', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));
      httpMock.expectOne(url).flush('forbidden', { status: 403, statusText: 'Forbidden' });
      await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
    });

    it('maps 404 to notFound', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));
      httpMock.expectOne(url).flush('not found', { status: 404, statusText: 'Not Found' });
      await expect(resultPromise).resolves.toEqual({ kind: 'notFound' });
    });

    it('maps an unexpected status to a generic error, carrying the echoed correlation id', async () => {
      const resultPromise = firstValueFrom(service.getIntent(rackId));
      httpMock.expectOne(url).flush('boom', {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'X-Correlation-Id': 'corr-1' },
      });
      await expect(resultPromise).resolves.toEqual({
        kind: 'error',
        status: 500,
        correlationId: 'corr-1',
      });
    });
  });

  describe('saveIntent', () => {
    it('sends the If-Match header when an etag is provided, and maps a 200 to ok', async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, 'etag-1'));

      const req = httpMock.expectOne(url);
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual(request);
      expect(req.request.headers.get('If-Match')).toBe('etag-1');
      req.flush(dto, { status: 200, statusText: 'OK', headers: { ETag: 'etag-2' } });

      await expect(resultPromise).resolves.toEqual({
        kind: 'ok',
        value: { intent: dto, etag: 'etag-2' },
      });
    });

    it("omits the If-Match header when ifMatch is null (a rack's first-ever save)", async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, null));

      const req = httpMock.expectOne(url);
      expect(req.request.headers.has('If-Match')).toBe(false);
      req.flush(dto, { status: 200, statusText: 'OK' });

      await resultPromise;
    });

    it('maps a 400 response to validationError, parsing ValidationProblemDetails-shaped field errors', async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, 'etag-1'));

      const req = httpMock.expectOne(url);
      req.flush(
        { errors: { 'vlanCatalogue[0].id': ['VLAN ID 10 already exists in this rack.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      await expect(resultPromise).resolves.toEqual({
        kind: 'validationError',
        errors: [
          { field: 'vlanCatalogue[0].id', messages: ['VLAN ID 10 already exists in this rack.'] },
        ],
      });
    });

    it('maps a 400 response with no errors body to an empty validationError list', async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, 'etag-1'));

      httpMock.expectOne(url).flush(null, { status: 400, statusText: 'Bad Request' });

      await expect(resultPromise).resolves.toEqual({ kind: 'validationError', errors: [] });
    });

    it('maps a 409 response to conflict (stale concurrency)', async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, 'etag-1'));

      httpMock.expectOne(url).flush('conflict', { status: 409, statusText: 'Conflict' });

      await expect(resultPromise).resolves.toEqual({ kind: 'conflict' });
    });

    it('maps a 403 response to forbidden', async () => {
      const resultPromise = firstValueFrom(service.saveIntent(rackId, request, 'etag-1'));

      httpMock.expectOne(url).flush('forbidden', { status: 403, statusText: 'Forbidden' });

      await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
    });
  });

  describe('validate', () => {
    const validateUrl = `${url}/validate`;

    it('maps a 200 response to ok', async () => {
      const resultPromise = firstValueFrom(service.validate(rackId, request));

      const req = httpMock.expectOne(validateUrl);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(request);
      req.flush({ isValid: true, errors: [] });

      await expect(resultPromise).resolves.toEqual({
        kind: 'ok',
        value: { isValid: true, errors: [] },
      });
    });

    it('maps a 403 response to forbidden', async () => {
      const resultPromise = firstValueFrom(service.validate(rackId, request));
      httpMock.expectOne(validateUrl).flush('forbidden', { status: 403, statusText: 'Forbidden' });
      await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
    });

    it('maps a 404 response to notFound', async () => {
      const resultPromise = firstValueFrom(service.validate(rackId, request));
      httpMock.expectOne(validateUrl).flush('not found', { status: 404, statusText: 'Not Found' });
      await expect(resultPromise).resolves.toEqual({ kind: 'notFound' });
    });
  });
});
