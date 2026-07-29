// A single root source of truth for "can this principal apply a drift correction" (AC3), so every
// Apply-adjacent surface (the details view's apply-action slot, a future bulk-apply entry point) reads
// one signal instead of each re-deriving it from the access token independently. The server remains the
// sole enforcement point (403 on a missing DriftApply claim) — this is a UX-only gate.
import { Injectable, inject, signal } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { take } from 'rxjs';
import { hasDriftApplyPermission } from './role.guard';

@Injectable({ providedIn: 'root' })
export class DriftPermissionService {
  private readonly oidc = inject(OidcSecurityService);

  private readonly _canApplyDrift = signal(false);
  readonly canApplyDrift = this._canApplyDrift.asReadonly();

  constructor() {
    // Read once at first injection: by the time any drift route is reachable, roleGuard has already
    // resolved authentication, so the access token payload is available synchronously-enough for a
    // one-shot read rather than a continuously-subscribed stream.
    this.oidc
      .getPayloadFromAccessToken()
      .pipe(take(1))
      .subscribe((payload) => this._canApplyDrift.set(hasDriftApplyPermission(payload)));
  }
}
