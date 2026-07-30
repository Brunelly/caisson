// CanDeactivate guard for the VLAN Catalogue/Port Intent routes (story #168, AC4). Functional, matching
// role.guard.ts's style. Only opens a confirmation when NetworkIntentStateService.dirty() is true — a
// CDK Dialog confirm (ADR 0034), not `window.confirm`, so focus-trap/Escape/backdrop/ARIA come from the
// shared primitive rather than a hand-rolled one.
import { Dialog } from '@angular/cdk/dialog';
import { inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { map, of } from 'rxjs';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { DiscardChangesDialogComponent } from './discard-changes-dialog.component';

// Deliberately NOT typed as `CanDeactivateFn<unknown>` here: that alias's call signature returns the
// full recursive `MaybeAsync<GuardResult>` union, which app.routes.ts/app.routes.prod.ts's dynamic
// `import(...).then(m => m.unsavedNetworkIntentChangesGuard(...))` wrapper (for code-splitting) cannot
// re-flatten through a `Promise.then` without TypeScript rejecting the nested-Observable branch. Letting
// this function's own return type infer as the narrower `Observable<boolean>` — always returning one,
// never a bare boolean — sidesteps that, and it is still structurally assignable everywhere a
// `CanDeactivateFn<unknown>` is expected (Angular's `Route.canDeactivate` array).
export const unsavedNetworkIntentChangesGuard = (): Observable<boolean> => {
  const state = inject(NetworkIntentStateService);
  if (!state.dirty()) {
    return of(true);
  }

  const dialog = inject(Dialog);
  const ref = dialog.open<boolean>(DiscardChangesDialogComponent, {
    ariaLabelledBy: 'discard-changes-dialog-heading',
    hasBackdrop: true,
    backdropClass: 'cds-overlay-backdrop',
    ariaModal: true,
  });
  return ref.closed.pipe(map((result) => result === true));
};
