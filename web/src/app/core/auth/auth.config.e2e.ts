// Story #11: the e2e-smoke build of provideCaissonAuth(), swapped in for auth.config.ts ONLY by the
// `e2e` build configuration (angular.json fileReplacements — the same mechanism `production` uses for
// environment.prod.ts/app.routes.prod.ts). Provides a fake OidcSecurityService that mints a static
// "authenticated as ReadOnly" state instead of running a real OIDC flow, so role.guard.ts's client-side
// check passes and auth.interceptor.ts attaches a bearer token to every API call. The live Caisson.Api
// this points at (environment.e2e.ts's apiBaseUrl) runs its own environment-gated test-auth scheme
// (ADR 0018) and mints its own fixed, non-privileged principal regardless of the token's actual
// content, so no real token generation is needed here — only enough to satisfy the guard/interceptor.
//
// This file is never imported by, and never compiled into, the `production` or `development` bundles —
// verified by grepping the built output for this file's marker string (`e2e-fake-token`).
import { makeEnvironmentProviders } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { of } from 'rxjs';

const E2E_FAKE_OIDC: Pick<
  OidcSecurityService,
  'isAuthenticated$' | 'getPayloadFromAccessToken' | 'getAccessToken'
> = {
  isAuthenticated$: of({ isAuthenticated: true, allConfigsAuthenticated: [] }),
  getPayloadFromAccessToken: () => of({ roles: ['ReadOnly'] }),
  getAccessToken: () => of('e2e-fake-token'),
};

export function provideCaissonAuth() {
  return makeEnvironmentProviders([{ provide: OidcSecurityService, useValue: E2E_FAKE_OIDC }]);
}
