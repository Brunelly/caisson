# 0043 — Responsive shell: mobile nav drawer and read-only rack chip over mock fidelity

## Status

Accepted

## Context

Story #123 makes the whole app usable at the Caisson Design System's `sm`/`md` breakpoints, starting
with the shell (app-shell, sidebar nav, top bar) that every routed page renders inside. The
`dark_CaissonAppShell_web.html` mock's mobile state shows two things this story deliberately does not
reproduce verbatim:

1. A horizontal "mobile-nav chip strip" as a second, independent list of nav items duplicating the
   sidebar's Topology/Drift/Settings entries for narrow viewports.
2. An implied rack-selector affordance that reads as interactive in the mock.

Neither is buildable without introducing a defect or inventing scope:

- The app has exactly two real, routed nav destinations (Topology, Drift) plus disabled placeholders
  (`sidebar-navigation.component.ts`'s own header comment already documents this constraint). A second,
  independently-rendered chip-strip nav would either drift out of sync with `SidebarNavigationComponent`
  over time (two lists to update in lockstep) or need to be generated from the same source, at which
  point it is strictly extra surface area for zero behavioural gain over reusing the one component.
- `rack-selector-topbar.component.ts` already has a standing, load-bearing decision (its own header
  comment, predating this story) that the rack "selector" is a **read-only chip**, not a dropdown: there
  is no rack-listing API, and `app.routes.ts` documents rack selection/listing as out of scope. Building
  a mobile dropdown affordance for it now would resurrect exactly the defect that comment already rejects
  for desktop.

Separately, the mock's mobile layout implies the sidebar becomes reachable through some off-canvas
mechanism, but doesn't specify the interaction primitive. This app already has an established modal
primitive for exactly this shape of problem — `@angular/cdk/dialog`'s `Dialog`, used by
`apply-confirmation-dialog.component.ts` (ADR 0034) — which gives focus-trap, `role="dialog"`/
`aria-modal`, Escape-to-close, a backdrop, and focus-restore-to-trigger for free, with zero bespoke
overlay/outside-click logic to write or maintain.

## Decision

- **Reuse `SidebarNavigationComponent` verbatim inside a CDK Dialog drawer**, rather than building the
  mock's separate chip-strip nav. A new, thin wrapper (`shell/sidebar-navigation/nav-drawer.component.ts`)
  supplies only the drawer-specific chrome (glass-surface background, close button) and forwards
  `rackId` via `DIALOG_DATA` — the same pattern `apply-confirmation-dialog.component.ts` already
  established for passing data into a dynamically-opened CDK Dialog component (CDK Dialog does not bind a
  component's own `@Input()`/`input()`s the way a template-driven usage would). The drawer is opened from
  a new hamburger button in `rack-selector-topbar.component.ts`, visible only below `md` (where
  `app-shell.component.scss` hides the static in-flow sidebar column), and closes itself on the next
  `NavigationEnd` so selecting Topology/Drift dismisses it with no change to the sidebar's own
  `routerLink` markup. The drawer panel is positioned left-anchored/full-height via a `position: fixed`
  override on its `panelClass` (`.cds-nav-drawer-panel`, `styles.scss`) rather than a custom
  `PositionStrategy` — `position: fixed` lifts the panel out of CDK Dialog's default centering flex
  layout entirely, which is simpler than fighting it with a `GlobalPositionStrategy`.
- **Keep the rack selector a read-only chip at every breakpoint.** No dropdown, no new interactive
  affordance, on mobile or otherwise — only touch-target and wrap/reflow treatment
  (`rack-selector-topbar.component.scss`) so the existing chip never overflows the viewport.
- **Add a single `cds-touch-target` mixin** (`_cds-mixins.scss`) as the one source of truth for the
  NFR3 44×44 CSS px hit-target floor, applied (wrapped in `cds-respond-below(sm)` where desktop density
  must be preserved) to `.sidebar__nav-item`, `.theme-toggle__option`, the new hamburger, and
  `.topbar__rack` — rather than four components each hard-coding their own `min-height`/`min-width`.

## Consequences

- The shell will not be pixel-identical to `dark_CaissonAppShell_web.html`'s mobile state: no
  chip-strip nav, no mobile rack dropdown. Both are intentional, documented departures favouring "reuse
  the one real nav component" and "no new interactive surface without a backing API" over exact mock
  fidelity — the same category of trade-off ADR 0039/0041 already made for topology/drift.
- `SidebarNavigationComponent` gains a second call site (the static column, and now the drawer) but no
  new props, inputs, or behavioural branches — the drawer wrapper carries all the drawer-specific
  concerns, keeping the reused component genuinely unchanged.
- Any future control needing the 44px floor should `@include cds-touch-target` rather than hard-coding
  `min-height`/`min-width`, so the floor stays defined in exactly one place.
- `.cds-nav-drawer-panel` and `.cds-overlay-backdrop` are both global, unscoped rules in `styles.scss` —
  consistent with the existing precedent that CDK Dialog/Overlay append their panes/backdrops outside any
  component's encapsulated view, so a component-scoped selector would never match.
- `RackSelectorTopBarComponent` (rendered eagerly on every route via `AppShellComponent`) dynamically
  `import()`s `@angular/cdk/dialog` and `nav-drawer.component.ts` from `openNavDrawer` rather than
  statically importing `Dialog` the way `apply-action.component.ts` does. A static import was tried
  first and pushed the initial bundle ~27kB over its 500kB budget (a warning-free production build is a
  hard constraint) — CDK's Overlay/Dialog/a11y/portal machinery had never previously been part of the
  eager bundle, only the drift feature's lazy chunk. Dynamic import keeps that cost out of every desktop
  visitor's initial load for a control most of them never trigger; it lands in its own ~8kB (gzipped)
  lazy chunk instead, fetched on first hamburger click.
