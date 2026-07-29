import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import type { DiscoveryStatusDto } from '../model/topology-contracts';
import { DiscoveryStatusService } from './discovery-status.service';

describe('DiscoveryStatusService', () => {
  let service: DiscoveryStatusService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(DiscoveryStatusService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('fetches the discovery status and wraps it as an ok result', async () => {
    const status = { rackId: 'rack-1' } as unknown as DiscoveryStatusDto;
    const resultPromise = firstValueFrom(service.getStatus('rack-1'));

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/rack-1/discovery-status`);
    expect(req.request.method).toBe('GET');
    req.flush(status);

    await expect(resultPromise).resolves.toEqual({ kind: 'ok', value: status });
  });

  it('finding #19: percent-encodes a rackId containing reserved URL characters', async () => {
    const trickyRackId = 'rack/1?evil=1#frag';
    const resultPromise = firstValueFrom(service.getStatus(trickyRackId));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(trickyRackId)}/discovery-status`,
    );
    req.flush({});

    await resultPromise;
  });
});
