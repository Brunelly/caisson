// Development environment config. Values here are non-secret, public SPA config only (PKCE public
// clients need no client secret) — see ADR 0015. Never add secrets/keys to any environment file.
import type { Environment } from './environment.model';

const apiBaseUrl = 'https://localhost:5001';

export const environment: Environment = {
  production: false,
  apiBaseUrl,
  hubUrl: `${apiBaseUrl}/hubs/topology`,
  oidc: {
    authority: 'https://login.microsoftonline.com/<tenant-id>/v2.0',
    clientId: '<dev-spa-client-id>',
    redirectUri: 'http://localhost:4200/',
    postLogoutRedirectUri: 'http://localhost:4200/',
    scope: 'openid profile offline_access api://<api-app-id>/Topology.Read',
  },
};
