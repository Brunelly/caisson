# 0038 — Migrate topology & drift feature screens onto `--cds-*` tokens

## Status

Accepted

## Context

ADR 0037 (Story #119) deliberately scoped `--cds-*` to the new app-shell chrome only, leaving every
existing feature component (topology graph/search/details, drift list/detail/apply/audit, the shared
`StatusBadgeComponent`) reading the older `--color-*` layer in `_tokens.scss`. That file defines only
`:root` (light) and `[data-theme='dark']` — there is no `[data-theme='hc-dark']` block. `ThemeService`
(story #125) already sets `data-theme="hc-dark"` globally the moment a user picks High Contrast, so
today those screens silently fall through to their dark-theme `--color-*` values on top of the shell's
black hc-dark chrome: dark colours on a black background, with no themed high-contrast palette at all.

Story #120 requires WCAG AA in all three themes for exactly these screens, which is not achievable
without a real hc-dark palette. ADR 0037 already named the intended fix and explicitly deferred it:
"If a future story re-skins an existing feature screen onto the CDS, that is a deliberate, separate
migration (swap `--color-*` reads for `--cds-*` reads in that component's stylesheet)." This story is
that migration, for a bounded, closed set of ~13 topology/drift/badge component stylesheets (the
`core/access-denied` page, which also reads `--color-*`, is out of scope and untouched).

Two alternatives were considered and rejected:
- **Alias `--color-*` to `--cds-*`** (redefine the `--color-*` custom properties to `var(--cds-*)`
  values). Rejected: this is exactly the "silent re-skin of every out-of-scope feature screen" ADR 0037
  warned against — it would also affect `core/access-denied`, which this story must not touch, and
  keeps two token layers alive indefinitely rather than converging on one.
- **Add a third `hc-dark` block to `_tokens.scss`.** Rejected: this keeps `--color-*` as a permanent,
  parallel palette to `--cds-*` forever, duplicating colour decisions in two places and contradicting
  the "DS tokens ONLY" constraint this story is explicitly scoped to satisfy.

## Decision

Retarget every `var(--color-*)` read in the closed set of topology, drift, and shared-badge component
stylesheets directly onto the equivalent `--cds-*` custom property, per this mapping:

| `--color-*` | `--cds-*` |
|---|---|
| `--color-bg` | `--cds-bg-page` |
| `--color-bg-elevated` | `--cds-surface-elevated` |
| `--color-bg-hover` | `--cds-surface-elevated` (or `--cds-surface-sunken` where the resting surface is already `--cds-surface-elevated` — see below) |
| `--color-bg-active` | `--cds-surface-sunken` |
| `--color-border`, `--color-border-strong` | `--cds-border-default` (the DS ships a single neutral border tier) |
| `--color-text` | `--cds-text-primary` |
| `--color-text-muted` | `--cds-text-secondary` (or `--cds-text-disabled` for a genuine `:disabled` state) |
| `--color-link`, `--color-primary` | `--cds-primary` |
| `--color-primary-contrast` | `--cds-primary-contrast` |
| `--color-input-text`, `--color-search-placeholder` | `--cds-text-primary` / `--cds-text-secondary` via the new `cds-form-input` mixin |
| `--color-status-confirmed(-bg)` | `--cds-success-fg` / `--cds-success-bg` |
| `--color-status-ambiguous(-bg)` | `--cds-warning-fg` / `--cds-warning-bg` |
| `--color-status-unmapped(-bg)` | `--cds-error-fg` / `--cds-error-bg` |

`@include tokens.focus-visible-ring` becomes `@include cds-mixins.cds-focus-visible-ring` everywhere
(resolves `--cds-border-focus`, which — unlike `--color-focus-ring` — has a correct hc-dark value).
Two new mixins were added to `_cds-mixins.scss` (mixin-only; still never `@use`d directly from a
component, to avoid duplicating the ~6kB token block into every compiled component stylesheet):
- `cds-form-input`, the `--cds-*` equivalent of `tokens.form-input`, for the topology search box and
  the native drift-type `<select>`.
- `cds-status-notice`, a shared amber "needs attention" pairing, replacing three separate ad-hoc
  amber-text treatments (`.details-panel__notice`, `.apply-action__stale`, `.apply-dialog__write-warning`).

The DS's neutral surface ramp has only three tiers (`--cds-bg-page` / `--cds-surface-elevated` /
`--cds-surface-sunken`), one fewer than the old four-tier `--color-bg` → `-elevated` → `-hover` →
`-active` ramp, and their dark-theme hex values do not coincide with the old hover/active hex values.
Where a component's resting surface is already `--cds-surface-elevated` (the topology graph node, the
details-panel close button, the search-result dropdown), hover/press states use
`--cds-surface-sunken` instead of `--cds-surface-elevated` to stay visibly distinct from rest —
everywhere else, hover uses `--cds-surface-elevated` as a real step up from `--cds-bg-page`. This is a
deliberate, minor cosmetic divergence from pixel-identical hover/press colours (unlike the
`--cds-bg-page`/`--cds-surface-elevated`/`--cds-text-primary`/`--cds-text-secondary`/`--cds-border-default`/
status-fg/-bg pairs, whose dark/light hex values were transcribed 1:1 from the old `--color-*` palette,
so those — the values visible almost all of the time — are pixel-identical in dark and light).

No component `.ts` logic, inputs/outputs, templates, routes, or RBAC behaviour changed — this is a
token-read swap only. `_tokens.scss` itself, and the out-of-scope `core/access-denied` page that still
reads it, are left untouched.

## Consequences

- Topology and drift screens now render the DS's actual hc-dark palette instead of silently falling
  through to dark-theme colours on a black background — the load-bearing fix this story exists for.
- `_tokens.scss` becomes dead weight for every consumer except `core/access-denied`; a future story can
  fold that single remaining page onto `--cds-*` too and delete `_tokens.scss` entirely, but that is out
  of scope here.
- `--cds-*`'s dark/light semantic values were verified against the old `--color-*` values they replace
  (identical for surfaces/text/border/status pairs); they have not been independently re-verified for
  real-browser WCAG AA contrast as `--cds-*` values — Task #131/#132 in this same story extends the
  real-browser axe contrast gate to a third hc-dark pass specifically to catch this before it ships.
