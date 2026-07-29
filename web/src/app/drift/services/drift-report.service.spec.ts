import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { DriftReportDetailDto } from '../model/drift-contracts';
import { DriftReportService } from './drift-report.service';

describe('DriftReportService', () => {
  let service: DriftReportService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(DriftReportService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('fetches the latest drift report and wraps it as an ok result', async () => {
    const detail = { report: { driftReportId: 'r1' } } as unknown as DriftReportDetailDto;
    const resultPromise = firstValueFrom(service.getLatest(rackId));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/latest`);
    expect(req.request.method).toBe('GET');
    req.flush(detail);

    await expect(resultPromise).resolves.toEqual({ kind: 'ok', value: detail });
  });

  it('sends cursor/pageSize query params for history', async () => {
    const resultPromise = firstValueFrom(service.getHistory(rackId, 'cursor-1', 25));

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/drift/history`,
    );
    expect(req.request.params.get('cursor')).toBe('cursor-1');
    expect(req.request.params.get('pageSize')).toBe('25');
    req.flush({});

    await resultPromise;
  });

  it('sends severity/driftType/actionable/cursor/pageSize filters for getReportById', async () => {
    const resultPromise = firstValueFrom(
      service.getReportById(rackId, 'report-1', {
        severity: 'High',
        driftType: 'AccessVlanMismatch',
        actionable: true,
        cursor: 'c1',
        pageSize: 10,
      }),
    );

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/drift/reports/report-1`,
    );
    expect(req.request.params.get('severity')).toBe('High');
    expect(req.request.params.get('driftType')).toBe('AccessVlanMismatch');
    expect(req.request.params.get('actionable')).toBe('true');
    expect(req.request.params.get('cursor')).toBe('c1');
    expect(req.request.params.get('pageSize')).toBe('10');
    req.flush({});

    await resultPromise;
  });

  it('getReportById omits filter params entirely when none are supplied', async () => {
    const resultPromise = firstValueFrom(service.getReportById(rackId, 'report-1'));

    const req = httpMock.expectOne(
      (r) => r.url === `${environment.apiBaseUrl}/api/racks/${rackId}/drift/reports/report-1`,
    );
    expect(req.request.params.keys().length).toBe(0);
    req.flush({});

    await resultPromise;
  });

  it('fetches a single drift item by id', async () => {
    const resultPromise = firstValueFrom(service.getItemById(rackId, 'item-1'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/drift/items/item-1`,
    );
    expect(req.request.method).toBe('GET');
    req.flush({});

    await resultPromise;
  });

  it('finding #19: percent-encodes rackId/driftReportId/driftItemId containing reserved URL characters', async () => {
    const trickyRackId = 'rack/1?evil=1#frag';
    const trickyItemId = 'item/../../etc';

    const latestPromise = firstValueFrom(service.getLatest(trickyRackId));
    httpMock
      .expectOne(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/drift/latest`,
      )
      .flush({});
    await latestPromise;

    const itemPromise = firstValueFrom(service.getItemById(trickyRackId, trickyItemId));
    httpMock
      .expectOne(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/drift/items/${encodeURIComponent(trickyItemId)}`,
      )
      .flush({});
    await itemPromise;
  });

  it('maps a 403 response to a forbidden result instead of throwing', async () => {
    const resultPromise = firstValueFrom(service.getLatest(rackId));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/${rackId}/drift/latest`);
    req.flush('forbidden', { status: 403, statusText: 'Forbidden' });

    await expect(resultPromise).resolves.toEqual({ kind: 'forbidden' });
  });

  it('maps a 404 response to a notFound result', async () => {
    const resultPromise = firstValueFrom(service.getItemById(rackId, 'missing'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/drift/items/missing`,
    );
    req.flush('not found', { status: 404, statusText: 'Not Found' });

    await expect(resultPromise).resolves.toEqual({ kind: 'notFound' });
  });
});
