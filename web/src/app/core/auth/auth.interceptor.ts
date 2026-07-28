// Attaches the bearer token to every API call and generates/sends an X-Correlation-Id (NFR6), reading
// it back from the response so callers can correlate a request with server-side logs/telemetry. Scoped
// to `environment.apiBaseUrl` only — the OIDC provider's own token endpoint and any third-party request
// must never receive the API's bearer token.
import { HttpResponse, type HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { map, switchMap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TelemetryService } from '../telemetry/telemetry.service';

export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiBaseUrl)) {
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
        telemetry.recordCorrelation(echoed, req.url);
      }
      return event;
    }),
  );
};
