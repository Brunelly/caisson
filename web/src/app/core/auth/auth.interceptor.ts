// Attaches the bearer token to every API call and generates/sends an X-Correlation-Id (NFR6), reading
// it back from the response so callers can correlate a request with server-side logs/telemetry. Scoped
// to `environment.apiBaseUrl`'s origin only — the OIDC provider's own token endpoint and any
// third-party request (including a same-prefix-but-different-host lookalike) must never receive the
// API's bearer token.
import { HttpResponse, type HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { map, switchMap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TelemetryService } from '../telemetry/telemetry.service';

export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

const API_ORIGIN = new URL(environment.apiBaseUrl, globalThis.location?.origin).origin;

/** True only when `url`'s origin matches the API's — a prefix match (`startsWith`) would also accept a
 * lookalike host like `${apiBaseUrl}.attacker.example`. */
function isApiRequest(url: string): boolean {
  return new URL(url, globalThis.location?.origin).origin === API_ORIGIN;
}

/** Strips the entity-detail/history stable-key path segment before a request URL is logged (NFR3): a
 * NIC's stable key IS its MAC address, so the raw path must never reach client telemetry/console. */
export function redactLoggableUrl(url: string): string {
  const parsed = new URL(url, globalThis.location?.origin);
  const segments = parsed.pathname.split('/');
  const entitiesIndex = segments.indexOf('entities');
  if (entitiesIndex === -1 || segments.length <= entitiesIndex + 2) {
    return parsed.pathname;
  }
  const keyIndex =
    segments[entitiesIndex + 2] === 'history' ? entitiesIndex + 3 : entitiesIndex + 2;
  return [...segments.slice(0, keyIndex), ':stableKey'].join('/');
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!isApiRequest(req.url)) {
    return next(req);
  }

  const oidc = inject(OidcSecurityService);
  const telemetry = inject(TelemetryService);
  const correlationId = crypto.randomUUID();

  return oidc.getAccessToken().pipe(
    switchMap((token) => {
      const headers: Record<string, string> = { [CORRELATION_ID_HEADER]: correlationId };
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
      return next(req.clone({ setHeaders: headers }));
    }),
    map((event) => {
      if (event instanceof HttpResponse) {
        const echoed = event.headers.get(CORRELATION_ID_HEADER) ?? correlationId;
        telemetry.recordCorrelation(echoed, redactLoggableUrl(req.url));
      }
      return event;
    }),
  );
};
