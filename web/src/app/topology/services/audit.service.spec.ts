import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import { AuditService } from './audit.service';

describe('AuditService', () => {
  let service: AuditService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuditService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends from/to/cursor/pageSize query params', async () => {
    const resultPromise = firstValueFrom(
      service.getAudit(rackId, {
        from: '2026-01-01T00:00:00Z',
        to: '2026-01-02T00:00:00Z',
        cursor: 'c1',
        pageSize: 10,
      }),
    );

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/audit`,
    );
    expect(req.request.params.get('from')).toBe('2026-01-01T00:00:00Z');
    expect(req.request.params.get('to')).toBe('2026-01-02T00:00:00Z');
    expect(req.request.params.get('cursor')).toBe('c1');
    expect(req.request.params.get('pageSize')).toBe('10');
    req.flush({});

    await resultPromise;
  });

  it('omits query params entirely when none are supplied', async () => {
    const resultPromise = firstValueFrom(service.getAudit(rackId));

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/audit`,
    );
    expect(req.request.params.keys().length).toBe(0);
    req.flush({});

    await resultPromise;
  });

  it('finding #19: percent-encodes rackId containing reserved URL characters', async () => {
    const trickyRackId = 'rack/1?evil=1#frag';
    const resultPromise = firstValueFrom(service.getAudit(trickyRackId));

    httpMock
      .expectOne(
        (r) =>
          r.url === `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/audit`,
      )
      .flush({});

    await resultPromise;
  });

  it('maps a 403 response to a forbidden result instead of throwing', async () => {
    const resultPromise = firstValueFrom(service.getAudit(rackId));

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/audit`,
    );
    req.flush('forbidden', { status: 403, statusText: 'Forbidden' });

    await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
  });
});
