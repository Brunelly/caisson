import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { PrStatusDto } from './pr-status-contracts';
import { PrStatusService } from './pr-status.service';

describe('PrStatusService', () => {
  let service: PrStatusService;
  let httpMock: HttpTestingController;

  const rackId = 'rack-1';
  const statusUrl = `${environment.apiBaseUrl}/api/racks/${rackId}/git/pull-request`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(PrStatusService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('GETs the rack status and maps a 200 to ok', async () => {
    const dto: PrStatusDto = {
      hasPullRequest: true,
      pullRequestNumber: 7,
      pullRequestUrl: 'https://gh/pr/7',
      state: 'Merged',
      headSha: 'abc',
      checksConclusion: 'Success',
      failingChecksCount: 0,
      checksSummary: '{}',
      lastUpdated: '2026-07-31T00:00:00Z',
      lastChecked: '2026-07-31T00:00:00Z',
      lastPollFailureReason: null,
      canApply: true,
      gateReasonCode: 'Allowed',
    };
    const result = firstValueFrom(service.getStatus(rackId));

    const req = httpMock.expectOne(statusUrl);
    expect(req.request.method).toBe('GET');
    req.flush(dto);

    await expect(result).resolves.toEqual({ kind: 'ok', value: dto });
  });

  it('maps a 403 to forbidden (no metadata)', async () => {
    const result = firstValueFrom(service.getStatus(rackId));
    httpMock.expectOne(statusUrl).flush(null, { status: 403, statusText: 'Forbidden' });
    await expect(result).resolves.toEqual({ kind: 'forbidden' });
  });

  it('GETs events with a pageSize param', async () => {
    const result = firstValueFrom(service.getEvents(rackId, undefined, 25));
    const req = httpMock.expectOne(
      (r) => r.url === `${statusUrl}/events` && r.params.get('pageSize') === '25',
    );
    expect(req.request.method).toBe('GET');
    req.flush({ items: [], nextCursor: null });
    await expect(result).resolves.toEqual({ kind: 'ok', value: { items: [], nextCursor: null } });
  });
});
