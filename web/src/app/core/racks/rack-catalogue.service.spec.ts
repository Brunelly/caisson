import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { environment } from '../../../environments/environment';
import { RackCatalogueService } from './rack-catalogue.service';

describe('RackCatalogueService', () => {
  let service: RackCatalogueService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RackCatalogueService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shares and caches one successful catalogue request', async () => {
    const first = firstValueFrom(service.load());
    const second = firstValueFrom(service.load());
    const request = http.expectOne(`${environment.apiBaseUrl}/api/racks`);
    request.flush([{ id: 'rack-1', externalKey: 'R01', name: 'Rack One' }]);

    await expect(first).resolves.toMatchObject({ kind: 'ok' });
    await expect(second).resolves.toMatchObject({ kind: 'ok' });
    expect(service.racks()[0]?.name).toBe('Rack One');
    http.expectNone(`${environment.apiBaseUrl}/api/racks`);
  });

  it('allows a failed catalogue request to be retried', async () => {
    const failed = firstValueFrom(service.load());
    http
      .expectOne(`${environment.apiBaseUrl}/api/racks`)
      .flush('', { status: 500, statusText: 'Error' });
    await expect(failed).resolves.toMatchObject({ kind: 'error', status: 500 });

    const retried = firstValueFrom(service.load(true));
    http.expectOne(`${environment.apiBaseUrl}/api/racks`).flush([]);
    await expect(retried).resolves.toEqual({ kind: 'ok', value: [] });
  });
});
