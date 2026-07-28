/** Shape shared by environment.ts and environment.prod.ts — a key added to one and forgotten in the
 * other now fails to compile instead of only surfacing at runtime after a prod deploy. */
export interface Environment {
  production: boolean;
  apiBaseUrl: string;
  hubUrl: string;
  oidc: {
    authority: string;
    clientId: string;
    redirectUri: string;
    postLogoutRedirectUri: string;
    scope: string;
  };
  /** Story #11: when true, `provideCaissonAuth()` substitutes a fake `OidcSecurityService` that mints
   * a static ReadOnly-role token instead of a real OIDC flow, for the Playwright e2e smoke against a
   * live Caisson.Api running with its own environment-gated test-auth scheme (ADR 0018). Only ever set
   * by `environment.e2e.ts` — never present (or true) in `environment.ts`/`environment.prod.ts`. */
  e2eAuthBypass?: boolean;
}
