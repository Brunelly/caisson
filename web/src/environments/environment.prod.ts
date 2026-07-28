// Production environment config. Values here are non-secret, public SPA config only (PKCE public
// clients need no client secret) — see ADR 0015. Real deployments should replace these placeholders at
// build/deploy time; never add secrets/keys to any environment file.
export const environment = {
  production: true,
  apiBaseUrl: 'https://api.caisson.example.com',
  hubUrl: 'https://api.caisson.example.com/hubs/topology',
  oidc: {
    authority: 'https://login.microsoftonline.com/<tenant-id>/v2.0',
    clientId: '<prod-spa-client-id>',
    redirectUri: 'https://app.caisson.example.com/',
    postLogoutRedirectUri: 'https://app.caisson.example.com/',
    scope: 'openid profile offline_access api://<api-app-id>/Topology.Read',
  },
};
