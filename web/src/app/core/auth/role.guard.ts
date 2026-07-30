// RBAC route guard (AC6): reasons about the same four CaissonRoles the API validates
// (Caisson.Api.Security.CaissonRoles) via the 'roles' claim ASP.NET's JwtBearer/RoleClaimsTransformation
// also reads. Unauthenticated -> OIDC login redirect; authenticated but no recognised role -> the
// access-denied route, without ever fetching topology data.
import { inject } from '@angular/core';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { map, of, switchMap, take } from 'rxjs';

/** Mirrors Caisson.Api.Security.CaissonRoles.All — every role permitted to view topology/audit data. */
export const RECOGNISED_ROLES: readonly string[] = [
  'Admin',
  'Operator',
  'ReadOnly',
  'ServiceAccount',
];

/** Mirrors Caisson.Api.Security.RoleClaimsTransformation.RoleClaimType. */
export const ROLE_CLAIM_TYPE = 'roles';

export function extractRoles(payload: unknown): string[] {
  if (!payload || typeof payload !== 'object') {
    return [];
  }

  const raw = (payload as Record<string, unknown>)[ROLE_CLAIM_TYPE];
  if (Array.isArray(raw)) {
    return raw.filter((role): role is string => typeof role === 'string');
  }
  if (typeof raw === 'string') {
    return [raw];
  }
  return [];
}

export function hasRecognisedRole(payload: unknown): boolean {
  return extractRoles(payload).some((role) => RECOGNISED_ROLES.includes(role));
}

/** Mirrors Caisson.Api.Security.CaissonRoles.DriftApply — a deliberately elevated permission excluded
 * from `CaissonRoles.All`/`RECOGNISED_ROLES` (ADR 0032), so it is never implied by Operator or even
 * Admin and must be granted/mapped independently. The server is the sole enforcement point (403 on a
 * missing claim); this is a UX-only gate that hides/disables the Apply action for principals who would
 * be rejected anyway. */
export const DRIFT_APPLY_ROLE = 'DriftApply';

export function hasDriftApplyPermission(payload: unknown): boolean {
  return extractRoles(payload).includes(DRIFT_APPLY_ROLE);
}

/** Mirrors Caisson.Api.Security.CaissonRoles.NetworkConfigAuthor (story #168, formalised per #174) —
 * same rationale as DRIFT_APPLY_ROLE above: a dedicated, independently-revocable permission excluded
 * from RECOGNISED_ROLES, never implied by Operator/Admin. The server is the sole enforcement point
 * (403 on a missing claim); this is a UX-only gate for the network-intent authoring controls. */
export const NETWORK_CONFIG_AUTHOR_ROLE = 'NetworkConfigAuthor';

export function hasNetworkConfigAuthorPermission(payload: unknown): boolean {
  return extractRoles(payload).includes(NETWORK_CONFIG_AUTHOR_ROLE);
}

export const roleGuard: CanActivateFn = () => {
  const oidc = inject(OidcSecurityService);
  const router = inject(Router);

  return oidc.isAuthenticated$.pipe(
    take(1),
    switchMap(({ isAuthenticated }) => {
      if (!isAuthenticated) {
        oidc.authorize();
        return of<boolean | UrlTree>(false);
      }

      return oidc.getPayloadFromAccessToken().pipe(
        take(1),
        map((payload) => (hasRecognisedRole(payload) ? true : router.parseUrl('/access-denied'))),
      );
    }),
  );
};
