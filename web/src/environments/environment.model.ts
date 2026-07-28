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
}
