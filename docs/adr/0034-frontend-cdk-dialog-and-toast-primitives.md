# 0034 — Frontend CDK Dialog and toast primitives

## Status

Accepted

## Context

Story #67's Apply workflow (AC4/AC5) is the SPA's first modal confirmation and its first success/error
surface. Two choices needed to be made:

1. **The confirmation modal.** The existing CDK Overlay usage in `topology-search.component.ts` is a
   `CdkConnectedOverlay`/`CdkOverlayOrigin` combobox — anchored to its trigger, not a true modal (no
   focus trap, no `aria-modal`, closes on selection/outside-click but was never designed to gate a
   destructive confirmation). The apply confirmation needs: a focus trap, `role="dialog"`/`aria-modal`,
   Escape-to-close, a backdrop, and focus restored to the trigger on close — all of which
   `@angular/cdk/dialog` already provides, and `@angular/cdk` is already a project dependency (via the
   overlay package topology-search uses).
2. **Success/error feedback.** Nothing in the app currently surfaces a transient success/error message;
   the apply workflow's 201/202 (job created), 403 (defence-in-depth for a stale client permission gate),
   429 (rate-limited), and generic-error outcomes all need one.

## Decision

1. **`@angular/cdk/dialog`'s `Dialog` service** is the confirmation modal primitive
   (`ApplyConfirmationDialogComponent`, `drift/apply/apply-confirmation-dialog.component.ts`) — distinct
   from `topology-search.component.ts`'s anchored `CdkConnectedOverlay` pattern, which stays the gold
   standard for anchored dropdowns/comboboxes, not modals. `Dialog.open()` gives focus-trap,
   `role="dialog"`/`aria-modal="true"`, Escape-to-close, a backdrop (`hasBackdrop`, click-to-close), and
   focus restored to the invoking element for free, matching the interactive-UI baseline without
   hand-rolled overlay/focus-management code.
2. **A new, minimal, signal-backed `ToastService`** (`shared/toast/toast.service.ts`) is the app-wide
   success/error surface, mounted once via `ToastOutletComponent` in `app.html`. Two kinds only (success,
   error), success auto-dismisses, error persists until manually dismissed and optionally carries a
   `correlationId` (NFR4) for support. No third-party toast library — the requirements (two kinds, no
   stacking/animation contract beyond a plain list) don't justify a new dependency.

## Consequences

- Every future confirmation-gated write action in this app should reach for `@angular/cdk/dialog`
  first, the same way every future anchored dropdown should reach for the `CdkConnectedOverlay` pattern
  first — two different CDK primitives for two different interaction shapes, not competing choices for
  the same one.
- `ToastService`/`ToastOutletComponent` are intentionally generic (not drift-specific) so any future
  write action can reuse them without a new toast surface.
- The confirmation dialog's rollback-window line is conditional (drift item `details.rollbackWindowSeconds`
  if present, else non-numeric copy) since no read API currently returns the value — a backend follow-up,
  not a frontend gap; the dialog must not hardcode the story's illustrative 120-second example.
