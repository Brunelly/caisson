// Story #11 e2e-smoke environment config, swapped in only by the `e2e` build configuration
// (angular.json fileReplacements — the same mechanism the `production` configuration uses for
// environment.prod.ts). `apiBaseUrl`/`hubUrl` point at the CI-hosted Caisson.Api instance running with
// its own environment-gated test-auth scheme (ADR 0018); `e2eAuthBypass` makes `provideCaissonAuth()`
// substitute a fake OidcSecurityService instead of a real OIDC flow (see auth.config.ts) — no secrets,
// no real Entra tenant. Never used by `environment.ts`/`environment.prod.ts`.
//
// This is a build-time file (bundled for the browser, so it cannot read `process.env` at runtime like
// a Node script). The CI job that builds this configuration MUST run Caisson.Api on this exact origin —
// see .github/workflows/ci.yml's angular-e2e-smoke job and docs/simulation-harness.md.
import type { Environment } from './environment.model';

const apiBaseUrl = 'http://localhost:5000';

export const environment: Environment = {
  production: false,
  apiBaseUrl,
  hubUrl: `${apiBaseUrl}/hubs/topology`,
  oidc: {
    authority: '',
    clientId: '',
    redirectUri: '',
    postLogoutRedirectUri: '',
    scope: '',
  },
  e2eAuthBypass: true,
};
