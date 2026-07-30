# 0039 — Topology token-alias layer and re-skin scope

## Status

Accepted

## Context

Story #121 re-skins the topology map UI (D3 graph, legend, search, details panel, discovery widget)
onto the Caisson Design System's frosted-glass/elevation language, with a resolved Q&A decision on the
token source of truth: "introduce a minimal topology-specific token alias layer within DS, no
hard-coded values" (rather than reading global `--cds-*` tokens directly everywhere, or allowing local
overrides with per-component rationale).

The topology graph already reads `--cds-*` tokens directly (ADR 0038 migrated it off the older
`--color-*` layer), so a further alias layer is not needed to reach AA-compliant colour — it exists to
give the graph/legend/details-panel/discovery-widget SCSS **intent-named** tokens
(`--cds-topo-mapping-confirmed`, `--cds-topo-confidence-high`, `--cds-topo-lane-fill`,
`--cds-topo-glow-selection`, …) instead of reaching for the generic `--cds-success-fg` /
`--cds-warning-fg` names inline everywhere a mapping-state or confidence colour is needed. This also
gives a single place to retarget topology's visual language in a future rebrand without touching every
consuming stylesheet.

The design mock (`dark_RackTopologyGraph_web.html` et al.) renders the **confirmed** mapping state and
the graph's primary/edge accents in the DS's brand cyan (`--color-primary`), reserving green only for a
generic "live" badge. Adopting that literally would recolour the topology graph's confirmed/ambiguous/
unmapped states away from the green/amber/red semantics used everywhere else in the app (edge badges,
`StatusBadgeComponent`, the legend, drift severity) — a change with real contrast risk (ADR 0038 only
verified green/amber/red for AA across all three themes, including the new hc-dark palette) and no
functional upside, since this story is explicitly visual-only and must not "regress #10 topology
behaviour" or the semantics AC1 protects.

## Decision

Add `web/src/app/shared/styles/_cds-topology-tokens.scss`, a dedicated partial `@use`d exactly once,
globally, from `styles.scss` (never from a component — see `_cds-mixins.scss`'s header comment on why
`@use`-ing a real token block from a component blows the `anyComponentStyle` budget). It defines three
groups of aliases, every value a `var(--cds-*)` reference or a `color-mix(in srgb, var(--cds-*) N%,
transparent)` of one (never a literal), so `topology-drift-token-usage.spec.ts`'s existing guard regex
passes unchanged once the new/touched files are added to its file list:

- **Mapping-state aliases** (`--cds-topo-mapping-confirmed/-ambiguous/-unmapped`) — alias
  `--cds-success-fg`/`--cds-warning-fg`/`--cds-error-fg` 1:1. **Deliberately KEEPS green/amber/red**,
  diverging from the mock's cyan-for-confirmed treatment.
- **Confidence aliases** (`--cds-topo-confidence-high/-medium/-low`) — mirror
  `StatusBadgeComponent`'s existing kind→colour map so the graph, legend, and details panel never grow
  a second, competing confidence palette.
- **Lane aliases** (`--cds-topo-lane-fill`/`-lane-stroke`) — low-opacity `color-mix` over
  `--cds-surface-elevated`/`--cds-border-default`, for the new VLAN-grouping backdrop layer.
- **Glow tokens** (`--cds-topo-glow-confirmed/-drift/-selection`) — full `drop-shadow()` argument lists
  (offset-x offset-y blur-radius colour) for the new luminous state cues. `confirmed`/`drift` alpha
  values (25%/35%) are transcribed from the design mock's own `--sh-node-confirmed`/`--sh-node-drift`
  tokens, `color-mix`ed over the existing `--cds-success-fg`/`--cds-error-fg` so glow colour always
  matches the paired stroke colour. **`selection` spends the DS's brand cyan
  (`--cds-primary`) instead** — the one state with no existing colliding meaning, rather than reusing
  `glow-drift` the way the mock's `.node.sel` rule does (drift severity and "this is the thing you just
  selected" are unrelated concepts; conflating them would make a selected-but-not-drifted node look
  like it has a device fault). `hc-dark` gets a second, higher-alpha override block for all three glow
  tokens (stronger cue, consistent with that theme's already-higher-contrast shadow tokens).

Only **new** styling added by this story reads the `--cds-topo-*` aliases; every existing `--cds-*`
read across the topology/drift stylesheets is left untouched, keeping the change purely additive.

## Consequences

- Topology re-skin SCSS reads self-documenting, intent-named tokens instead of generic semantic ones,
  and a future rebrand of "what colour means confirmed/drift/selected" is a one-file change.
- The deliberate colour divergence from the mock (green/amber/red kept, cyan spent on selection instead
  of confirmed) means the re-skinned graph will not be pixel-identical to `dark_RackTopologyGraph_web.html`
  for the confirmed state — an intentional, documented trade-off favouring reused, already-AA-verified
  semantics over exact mock fidelity.
- `_cds-topology-tokens.scss` is a second small `:root`/`[data-theme='hc-dark']` block alongside
  `_cds-tokens.scss` and `_tokens.scss`; a future contributor adding a new topology alias must remember
  to add it to both the base and `hc-dark` blocks here, same convention as `_cds-tokens.scss`.
