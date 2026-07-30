// A single root source of truth for "can this principal author network intent" (story #168, mirrors
// DriftPermissionService/ADR 0032's precedent exactly). Every authoring-adjacent surface (VLAN Catalogue
// mutating controls, Port Intent editor) reads this one signal instead of each re-deriving it from the
// access token independently. The server remains the sole enforcement point (403 on a missing
// NetworkConfigAuthor claim) — this is a UX-only gate: mutating controls are ABSENT (not merely
// disabled) when this signal is false, matching apply-action.component.ts's gating style.
import { Injectable, inject, signal } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { take } from 'rxjs';
import { hasNetworkConfigAuthorPermission } from '../../core/auth/role.guard';

@Injectable({ providedIn: 'root' })
export class NetworkConfigPermissionService {
  private readonly oidc = inject(OidcSecurityService);

  private readonly _canAuthorNetworkConfig = signal(false);
  readonly canAuthorNetworkConfig = this._canAuthorNetworkConfig.asReadonly();

  constructor() {
    // Read once at first injection: by the time a network-config route is reachable, roleGuard has
    // already resolved authentication, so the access token payload is available synchronously-enough
    // for a one-shot read rather than a continuously-subscribed stream (mirrors DriftPermissionService).
    this.oidc
      .getPayloadFromAccessToken()
      .pipe(take(1))
      .subscribe((payload) =>
        this._canAuthorNetworkConfig.set(hasNetworkConfigAuthorPermission(payload)),
      );
  }
}
