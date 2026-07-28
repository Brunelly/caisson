// OIDC/Entra configuration (code + PKCE, silent renew). Configured against the same Entra tenant/app
// registration as the API's AzureAd:Authority/Audience (ADR 0015) — a public SPA client under PKCE
// needs no client secret. The token is held in memory only (InMemoryStorage below, overriding the
// library's localStorage default), reducing the XSS blast radius for sensitive MAC/topology data
// (NFR3): a hard refresh re-runs silent renew rather than reading a persisted token.
import { isDevMode, makeEnvironmentProviders } from '@angular/core';
import {
  AbstractSecurityStorage,
  provideAuth,
  withAppInitializerAuthCheck,
} from 'angular-auth-oidc-client';
import { environment } from '../../../environments/environment';

/** In-memory-only token storage (NFR3): nothing is written to `localStorage`/`sessionStorage`. */
export class InMemoryStorage implements AbstractSecurityStorage {
  private readonly store = new Map<string, string>();

  read(key: string): string | null {
    return this.store.get(key) ?? null;
  }

  write(key: string, value: string): void {
    this.store.set(key, value);
  }

  remove(key: string): void {
    this.store.delete(key);
  }

  clear(): void {
    this.store.clear();
  }
}

// Story #11: the e2e-smoke build configuration (`ng build --configuration e2e`) replaces this ENTIRE
// FILE with auth.config.e2e.ts via angular.json's fileReplacements, mirroring how the `production`
// configuration replaces environment.ts/app.routes.ts (ADR 0016 already established — and rejected the
// alternative — that a runtime `if (environment.someFlag)` branch here is NOT reliably tree-shaken out
// of the production bundle by the bundler, so the bypass code must live in a file that is simply never
// compiled into that bundle at all, not one that is compiled in but supposedly never executed).

export function provideCaissonAuth() {
  return makeEnvironmentProviders([
    provideAuth(
      {
        config: {
          authority: environment.oidc.authority,
          clientId: environment.oidc.clientId,
          redirectUrl: environment.oidc.redirectUri,
          postLogoutRedirectUri: environment.oidc.postLogoutRedirectUri,
          scope: environment.oidc.scope,
          responseType: 'code',
          silentRenew: true,
          useRefreshToken: true,
          // Read-only viewer (M0): a short renew window is fine, no long-lived write session to protect.
          renewTimeBeforeTokenExpiresInSeconds: 30,
          logLevel: isDevMode() ? 1 : 3, // Debug in dev, Error only in prod — never logs token contents.
          secureRoutes: [environment.apiBaseUrl],
        },
      },
      withAppInitializerAuthCheck(),
    ),
    { provide: AbstractSecurityStorage, useClass: InMemoryStorage },
  ]);
}
