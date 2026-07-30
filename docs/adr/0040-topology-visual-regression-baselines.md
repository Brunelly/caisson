# 0040 — Topology visual regression baselines (Playwright screenshots)

## Status

Accepted

## Context

Story #121's resolved Q&A picked the visual-regression strategy for this re-skin explicitly: "Add
Playwright screenshot tests for key topology states now" — rejecting both a manual-checklist-only
approach (no regression protection once this story closes) and standing up Storybook + a separate
visual-regression tool (new infra for a bounded, already-Playwright-equipped repo). The repo already
has two real-browser Playwright suites (`theme-shell.spec.ts`, `topology-harness.spec.ts`) exercising
the exact fixture-backed, fully-offline dev-harness route (`/__dev-harness__/topology/rack-1`, see
`web/src/app/dev-harness/`) this task needs — no live OIDC tenant, backend, or seeded rack data, and
deterministic fixture content (`dev-harness/fixtures.ts`).

Two further decisions had to be made once the Q&A settled "screenshots, now, against the harness":
whether to capture full-page screenshots or bounded per-element crops, and how to keep the new suite
from becoming flaky noise given the story's core deliverable (glow/glass/blur treatments) is exactly
the kind of styling most sensitive to sub-pixel/anti-aliasing drift between runs.

## Decision

Add `web/e2e/topology-visual.spec.ts`: for each of the three themes (dark/light/hc-dark, seeded via
`page.addInitScript` before navigation — the same pattern `theme-shell.spec.ts` already uses), capture
`toHaveScreenshot()` baselines for the states Task #135 calls out — default graph view, a selected node
(the new glow/ring highlight), the details panel open, the search dropdown open, the legend, three
discovery-widget states (succeeded/in-progress/failed, selected via the harness's new
`?discoveryStatus=` query param — see `dev-harness/fixtures.ts`'s `HarnessDiscoveryStatusVariant`), and
the stale/disconnected live-connection banner. 27 screenshots total.

Key choices:

- **Every screenshot is a bounded, per-locator crop** (`expect(locator).toHaveScreenshot(...)`), never
  a full-page screenshot. This keeps committed baseline PNGs small (~20–55 KB each, ~640 KB total for
  all 27) and keeps a diff meaningful — a full-page baseline would fail on any unrelated pixel anywhere
  on the page (e.g. a future, unrelated shell change), while a tight crop only fails when the thing this
  story actually re-skinned changes.
- **Timestamps are masked** (`mask: [...]`) on every screenshot that renders one (`.snapshot-meta`,
  `.djs-widget__meta`) — the harness fixture's dates are fixed, not wall-clock `now()`, but formatted
  presentation still depends on the runner's locale/timezone, which must never be what fails this gate.
- **`maxDiffPixelRatio: 0.01`** (`playwright.config.ts`) absorbs anti-aliasing/sub-pixel font-rendering
  noise between otherwise-identical runs on the same pinned browser, without masking a real regression:
  any genuine visual change (a moved element, a swapped colour, a missing glow) touches far more than1%
  of a tightly cropped screenshot.
- **`snapshotPathTemplate`** places baselines under `e2e/__screenshots__/<spec-file>/<name>-<platform>.png`
  — a stable, browsable location instead of Playwright's default (nested under `testDir` using the full
  spec path), making the curated baseline set easy to scan in a PR diff.
- **Baselines are generated from, and MUST be regenerated from, the pinned CI/mcp-tooling Linux Chromium
  build** — not a contributor's local machine, whose font rendering/subpixel AA differs enough to fail
  the pixel-ratio threshold even with an otherwise-identical DOM. Any future intentional visual change
  regenerates baselines via `playwright test e2e/topology-visual.spec.ts --update-snapshots` run against
  that same pinned browser/environment (the harness route + fixtures, not `E2E_BASE_URL`/a live backend
  — see `topology-harness.spec.ts`'s own header comment for why this repo's harness specs never need
  either).
- Extended (never forked) the existing `web/src/app/dev-harness/fixtures.ts` fake
  `DiscoveryStatusService` with the `?discoveryStatus=` query-param variant selector needed for the
  three discovery-widget screenshot states, mirroring the existing `?roles=` pattern
  (`dev-harness.providers.ts`'s `fakeOidc`) — this is in-repo Angular app/test code (the same file every
  other harness-route Playwright spec in this repo already depends on), not the separate
  mcp-tooling-caisson repo's backend-stack e2e tooling.

## Consequences

- The re-skin's most failure-prone styling (glow filters, glass blur, VLAN lanes, the new selection
  highlight) now has a real pixel-level regression gate, not just axe's structural/contrast checks.
- A future, unrelated topology change that moves/resizes any of the seven captured surfaces requires a
  deliberate `--update-snapshots` regeneration + review of the new baseline PNG in the same PR — a small
  but real ongoing maintenance cost, accepted as the trade-off the resolved Q&A already chose over no
  visual regression coverage at all.
- Baselines are Linux-Chromium-specific; anyone regenerating them on a different OS/browser will produce
  baselines that fail in CI. `playwright.config.ts`'s header comment on `snapshotPathTemplate` and this
  ADR are the two places that call this out.
