import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import { TopologyEntityService, encodeStableKeyPath } from './topology-entity.service';

describe('encodeStableKeyPath', () => {
  it('leaves a simple key untouched', () => {
    expect(encodeStableKeyPath('sw-1')).toBe('sw-1');
  });

  it('percent-encodes each segment of a slash-bearing key without escaping the separators', () => {
    // The `{**stableKey}` catch-all route relies on '/' remaining literal to see multiple segments.
    expect(encodeStableKeyPath('Ethernet1/0/1')).toBe('Ethernet1/0/1');
  });

  it('percent-encodes reserved characters within a segment, not just slashes', () => {
    expect(encodeStableKeyPath('sw-1/port #4')).toBe('sw-1/port%20%234');
  });
});

describe('TopologyEntityService', () => {
  let service: TopologyEntityService;
  let httpMock: HttpTestingController;
  const rackId = 'rack-1';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TopologyEntityService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('builds the entity URL with each stable-key segment encoded', async () => {
    const resultPromise = firstValueFrom(service.getEntity(rackId, 'SwitchPort', 'Ethernet1/0/1'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/entities/SwitchPort/Ethernet1/0/1`,
    );
    expect(req.request.method).toBe('GET');
    req.flush({});

    await resultPromise;
  });

  it('builds the history URL under the /history segment', async () => {
    const resultPromise = firstValueFrom(
      service.getEntityHistory(rackId, 'Nic', 'aa:bb:cc:dd:ee:ff'),
    );

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/entities/Nic/history/aa%3Abb%3Acc%3Add%3Aee%3Aff`,
    );
    req.flush([]);

    await resultPromise;
  });

  it('maps a 401 to an unauthorized result', async () => {
    const resultPromise = firstValueFrom(service.getEntity(rackId, 'Server', 'srv-1'));

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/${rackId}/topology/entities/Server/srv-1`,
    );
    req.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    await expect(resultPromise).resolves.toEqual({ kind: 'unauthorized' });
  });
});
