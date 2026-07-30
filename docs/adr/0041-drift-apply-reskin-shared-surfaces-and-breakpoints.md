# 0041 — Drift/apply re-skin: shared surfaces, banner, and breakpoint mixins

## Status

Accepted

## Context

Story #122 re-skins the drift overlay, drift report list/detail views, apply confirmation dialog, live
job-status timeline, and audit record view onto the Caisson Design System, mirroring the #121 topology
re-skin's approach: introduce the minimum genuinely-new, cross-cutting styling infrastructure once, in
`_cds-mixins.scss`, before touching any individual component.

Three needs recur across every drift/apply surface once the `*_web.html` mocks are read closely:

1. **An opaque elevated panel surface.** Every mock (`ApplyConfirmationDialog`, `DriftReportsList`,
   `DriftReportDetailsView`, `AuditRecordView`) renders its outer wrapper as a flat
   `--surface-elevated` fill with a 1px border and a soft shadow — never `backdrop-filter`. This
   deliberately continues the reasoning already recorded in ADR 0039/0038's re-skin scoping: glass
   (`cds-glass-surface`) is reserved for floating shell chrome (sidebar/topbar), not per-feature-page
   panels, both for visual fidelity to the mocks and because blurring behind a 500-row drift list or a
   live-updating job timeline is exactly the kind of expensive, frequently-repainted filter NFR4 warns
   against.
2. **A tone-parameterized banner.** The existing `cds-status-notice` mixin (amber/warning only) covers
   the apply dialog's "this is a live write" banner and the stale-drift notice, but the dialog mock also
   has a cyan/info "automatic rollback is armed" banner with an identical shape (icon + text, tokened
   bg/fg pair, `--cds-radius-md`) — only the tone token differs.
3. **A responsive breakpoint convention.** The dialog/list/detail mocks all define real breakpoint
   behaviour (stacked summary rows below `md`, single-column footer below `sm`, filter-bar wrap at
   `md`). A repo-wide grep at the start of this story confirmed there is still no `@media` rule anywhere
   in `web/src/`, so this story is the first to need one — worth establishing as a named, DS-tokened
   helper once rather than five components each hand-rolling their own `max-width` breakpoints.

A fourth question — whether to add a `_cds-drift-tokens.scss` alias layer, mirroring `_cds-topology-
tokens.scss` (ADR 0039) — was considered and rejected. Unlike the topology re-skin, nothing in this
story's diff needs an intent-named alias: every drift/apply surface only ever needs a base semantic
token (`--cds-success/-warning/-error/-info-bg/-fg`) or the plain `--cds-primary`/`--cds-surface-*`
tokens directly, with no repeated "what does this colour mean here" naming problem the way topology's
mapping-state/glow tokens had. ADR 0037 already warns against token-layer sprawl; adding an alias file
with no distinct consumer need would be exactly that.

## Decision

Add three mixins to `web/src/app/shared/styles/_cds-mixins.scss` (mixin-only, `@use`d per-component —
see the file's own header comment on why the real token files are never `@use`d from a component):

- **`cds-elevated-card`** — `background: var(--cds-surface-elevated)`, `border: 1px solid
  var(--cds-border-default)`, `border-radius: var(--cds-radius-lg)`, `box-shadow: var(--cds-shadow-
  md)`. Applied to the apply dialog panel, the drift list/detail/audit page wrappers.
- **`cds-banner($tone)`** — resolves to `var(--cds-#{$tone}-bg)` / `var(--cds-#{$tone}-fg)` (radius +
  padding + font-size held constant). `cds-status-notice` is refactored to `@include cds-banner
  (warning)` so its three existing call sites (`apply-action`, `topology-details-panel`, and now also
  `apply-confirmation-dialog`'s write-warning) need zero changes. The dialog's new rollback banner uses
  `cds-banner(info)`.
- **`cds-respond-below($breakpoint)`** — emits `@media (max-width: $width - 1px) { @content }` from an
  SCSS map transcribed 1:1 from the DS tokens JSON's `platforms.web.breakpoints` (sm 640 / md 768 /
  lg 1024 / xl 1280 / 2xl 1536). `map.get`/`map.keys` via `@use 'sass:map'` (not the legacy global
  `map-get`/`map-keys` functions) to stay warning-free under current Dart Sass.

No `_cds-drift-tokens.scss` file is introduced (see Context above) — drift/apply surfaces read base
`--cds-*` semantic tokens plus these three mixins directly.

**Icons.** The re-skin continues the existing unicode-glyph vocabulary (`StatusBadgeComponent`'s ✓/▲/✕/
…/↻ icons) rather than adopting the mocks' inline Lucide SVGs or a new icon-library dependency. Where a
mock's icon is purely decorative chrome with no existing glyph equivalent (e.g. the dialog header's
alert-triangle, the summary row's field-type icons), it is reproduced as an inline `aria-hidden="true"`
SVG — decorative only, never a second focusable/interactive element, never conveying meaning that isn't
already carried by text.

**Deliberate mock-fidelity trade-offs** (apply-confirmation-dialog, Task #138):

- The mock's header close-`X` button is **omitted**. Cancel, Escape, and backdrop-click already close
  the dialog with identical (no-op, no API call) semantics (see the component's own header comment); a
  fourth close affordance is an *additional action* the story's AC1 explicitly forbids ("no additional
  actions become available"), not a value-neutral visual tweak.
- The mock's decomposed Switch / Port / Server NIC summary rows are **not** reproduced field-for-field.
  The underlying `DriftItemDto` exposes a single opaque `subjectKey` string (formatted per
  `subjectType`), not separately-addressable switch/port/NIC fields — inventing that decomposition here
  would be a data-shape change disguised as styling, out of scope for a visual-only story.

## Consequences

- Five components (`apply-confirmation-dialog`, `job-status-timeline`, `drift-reports-list`,
  `drift-report-details`, `audit-record-view`) share one opaque-panel definition instead of five
  hand-rolled `background`/`border`/`border-radius`/`box-shadow` blocks.
- The dialog's info banner and its existing warning banner are visually consistent (same shape, tone-
  swapped) for free, and any future third banner tone (`cds-banner(error)`/`cds-banner(success)`) is a
  one-line addition at the call site.
- `cds-respond-below` is the app's first responsive convention; any component migrated onto the DS after
  this story should prefer it over a hand-written `@media (max-width: ...)` block so breakpoints stay in
  lockstep with the DS tokens JSON in exactly one place.
- The apply dialog will not be pixel-identical to `dark_ApplyConfirmationDialog_web.html`: no close-X,
  and the summary renders `subjectType`/`subjectKey` as today rather than three decomposed rows — both
  intentional, documented departures favouring "no new interactive surface" and "no data-shape
  invention" over exact mock fidelity, matching the same kind of trade-off ADR 0039 already made for
  topology's confirmed-state colour.
