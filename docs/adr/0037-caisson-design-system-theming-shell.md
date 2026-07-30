# 0037 — Caisson Design System theming shell

## Status

Accepted

## Context

Story #119 introduces the Caisson Design System (CDS): a dark-first token set (cyan/green accents,
Inter/JetBrains Mono typography) with three themes — dark (default), light, and a curated
high-contrast-dark — delivered as `design-system-caisson-design-syste-tokens.json` plus rendered shell
component previews (CaissonAppShell, SidebarNavigation, RackSelectorTopBar, ToastOutlet,
LiveConnectionStatusBar). The story is explicitly scoped to the app **shell/chrome only** — existing
feature screens (topology graph, drift badges/tables) are out of scope and must keep their current
visuals and behaviour unchanged. Three decisions had to be made before writing any shell component:

1. Whether the new DS tokens replace/alias the existing `--color-*` tokens (`_tokens.scss`, ADR 0015)
   or live alongside them.
2. What enum/string to use for the theme value, given the design preview HTML uses
   `data-theme="high-contrast-dark"` while the story's own persistence example uses `hc-dark`.
3. Whether to honour the four rendered preview HTMLs — which render flat, opaque surfaces — or the
   story's written text/AC3, which mandates translucent "enterprise-glass" panels with backdrop-blur.

## Decision

1. **`--cds-*` is a new, parallel token layer, not an alias of `--color-*`.** The DS primary is cyan
   (`#06B6D4`) vs. the app's existing blue (`#2563eb`); aliasing `--color-*` to `--cds-*` would silently
   re-skin every out-of-scope feature screen the moment the new stylesheet loaded. New shell components
   (`app-shell`, `sidebar-navigation`, `rack-selector-topbar`, `theme-toggle`,
   `live-connection-status-bar`, the re-skinned `toast-outlet`) read `--cds-*` exclusively; every
   existing feature component keeps reading `--color-*`, completely untouched. Tokens live in a new
   `web/src/app/shared/styles/_cds-tokens.scss` partial (not folded into `_tokens.scss`) to avoid
   churning the existing file, imported once from `styles.scss` alongside it.
2. **The enum is `'dark' | 'light' | 'hc-dark'`, used verbatim for both the persisted
   `localStorage['caisson.theme']` value and the `data-theme` DOM attribute.** One string, no
   translation layer — even though the design preview HTML's own `data-theme` uses
   `"high-contrast-dark"`. This keeps `ThemeService` (story-125) trivial: read the attribute, validate
   against the enum, done.
3. **The written glass/mesh/scrim requirement is authoritative over the flat preview HTMLs.** The story
   text, ACs, and Task #127 all explicitly require translucent panels (`backdrop-filter` blur over a
   semi-opaque fill, ≥0.72 dark / ≥0.85 light) with a scrim guaranteeing WCAG AA text contrast; the
   preview HTMLs appear to have been rendered without `backdrop-filter` support in the render pipeline.
   Shell surfaces use `--cds-glass-fill` / `--cds-glass-blur` and a background mesh at
   `--cds-mesh-opacity`, all three collapsing to opaque/zero/zero under `[data-theme='hc-dark']`
   purely via token values — no `if (theme === 'hc-dark')` branches in component code. **Flagged for
   design review** in case the flat previews were in fact the intended final visual, not a rendering
   limitation.
4. No external font `<link>`/CDN is introduced (the design preview HTML loads Google Fonts) — `
   --cds-font-base`/`--cds-font-mono` list Inter/JetBrains Mono as the first preference with a full
   system-font fallback stack, matching this app's enterprise/air-gapped deployment constraint.

## Consequences

- Every future shell-owned component must import `_cds-tokens.scss`'s tokens/mixins and must never
  introduce a hex/rgb/hsl literal outside that file — enforced mechanically by `token-usage.spec.ts`
  (Task #128).
- If a future story re-skins an existing feature screen onto the CDS, that is a deliberate, separate
  migration (swap `--color-*` reads for `--cds-*` reads in that component's stylesheet) — not something
  that happens implicitly via this token layer.
- Because Inter/JetBrains Mono are not bundled or loaded from a CDN, shell typography renders in the
  system-font fallback unless/until the fonts are self-hosted in a later story; this is intentional for
  the enterprise/air-gapped constraint, not an oversight.
- The glass-over-flat-preview judgement in point 3 should be confirmed with design; if the previews are
  authoritative instead, the `--cds-glass-*`/`--cds-mesh-opacity` tokens would need to move to
  fully-opaque values with no behavioural change elsewhere (token-only fix).
