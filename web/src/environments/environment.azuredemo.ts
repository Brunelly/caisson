// Azure Container Apps TEST-demo environment. Swapped in by the `azuredemo` build configuration
// (angular.json fileReplacements), which — like the `e2e` configuration — also replaces auth.config.ts
// with auth.config.e2e.ts so `provideCaissonAuth()` substitutes a fake OidcSecurityService instead of a
// real Entra OIDC flow. This lets the deployed SPA be browsed read-only against the deployed Caisson.Api
// (running with its environment-gated test-auth scheme, ADR 0018) WITHOUT a real Entra tenant.
//
// This is a deliberate, clearly-labelled DEMO build for the single TEST environment — never production.
// `apiBaseUrl` points at the deployed Caisson.Api container app; when a real Entra integration and a
// custom domain exist, this file (and the auth bypass) go away in favour of `environment.prod.ts`.
import type { Environment } from './environment.model';

const apiBaseUrl = 'https://by-azuks-app-caisson-api.lemontree-7bffb5af.uksouth.azurecontainerapps.io';

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
