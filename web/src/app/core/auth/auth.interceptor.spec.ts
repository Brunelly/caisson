import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { firstValueFrom, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { environment } from '../../../environments/environment';
import { TelemetryService } from '../telemetry/telemetry.service';
import { authInterceptor, redactLoggableUrl } from './auth.interceptor';

describe('redactLoggableUrl', () => {
  it('redacts an entity-detail stable key (a NIC MAC) from the path (NFR3)', () => {
    expect(
      redactLoggableUrl(
        `${environment.apiBaseUrl}/api/racks/rack-1/topology/entities/Nic/aabbccddeeff`,
      ),
    ).toBe('/api/racks/rack-1/topology/entities/Nic/:stableKey');
  });

  it('redacts a slash-bearing stable key spanning multiple path segments', () => {
    expect(
      redactLoggableUrl(
        `${environment.apiBaseUrl}/api/racks/rack-1/topology/entities/SwitchPort/Ethernet1/0/1`,
      ),
    ).toBe('/api/racks/rack-1/topology/entities/SwitchPort/:stableKey');
  });

  it('redacts an entity-history stable key', () => {
    expect(
      redactLoggableUrl(
        `${environment.apiBaseUrl}/api/racks/rack-1/topology/entities/Nic/history/aabbccddeeff`,
      ),
    ).toBe('/api/racks/rack-1/topology/entities/Nic/history/:stableKey');
  });

  it('leaves non-entity URLs (rack/snapshot ids are not sensitive) unchanged', () => {
    expect(
      redactLoggableUrl(`${environment.apiBaseUrl}/api/racks/rack-1/topology/snapshots/latest`),
    ).toBe('/api/racks/rack-1/topology/snapshots/latest');
  });
});

describe('authInterceptor', () => {
  let httpMock: HttpTestingController;
  let http: HttpClient;
  let getAccessToken: ReturnType<typeof vi.fn>;
  let recordCorrelation: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getAccessToken = vi.fn(() => of('secret-token'));
    recordCorrelation = vi.fn();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: OidcSecurityService, useValue: { getAccessToken } },
        { provide: TelemetryService, useValue: { recordCorrelation } },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('attaches the bearer token and correlation id to a same-origin API request', async () => {
    const resultPromise = firstValueFrom(
      http.get(`${environment.apiBaseUrl}/api/racks/rack-1/topology`),
    );

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/api/racks/rack-1/topology`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer secret-token');
    expect(req.request.headers.get('X-Correlation-Id')).toBeTruthy();
    req.flush({});

    await resultPromise;
  });

  it('does not attach the bearer token to a request for a different origin', async () => {
    const resultPromise = firstValueFrom(http.get('https://third-party.example/data'));

    const req = httpMock.expectOne('https://third-party.example/data');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});

    await resultPromise;
    expect(getAccessToken).not.toHaveBeenCalled();
  });

  it('does not attach the bearer token to a lookalike host that merely starts with the API origin', async () => {
    // A prefix match (`url.startsWith(apiBaseUrl)`) would wrongly accept this: the string
    // 'https://localhost:50011' starts with 'https://localhost:5001', but it is a different origin
    // (a different port), so an origin-boundary check must reject it.
    const lookalike = `${environment.apiBaseUrl}1/api/racks/rack-1/topology`;
    const resultPromise = firstValueFrom(http.get(lookalike));

    const req = httpMock.expectOne(lookalike);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});

    await resultPromise;
    expect(getAccessToken).not.toHaveBeenCalled();
  });

  it('records the correlation id against a redacted URL, never the raw MAC-bearing path', async () => {
    const resultPromise = firstValueFrom(
      http.get(`${environment.apiBaseUrl}/api/racks/rack-1/topology/entities/Nic/aabbccddeeff`),
    );

    const req = httpMock.expectOne(
      `${environment.apiBaseUrl}/api/racks/rack-1/topology/entities/Nic/aabbccddeeff`,
    );
    req.flush({}, { headers: { 'X-Correlation-Id': 'corr-1' } });

    await resultPromise;
    expect(recordCorrelation).toHaveBeenCalledWith(
      'corr-1',
      '/api/racks/rack-1/topology/entities/Nic/:stableKey',
    );
  });
});
