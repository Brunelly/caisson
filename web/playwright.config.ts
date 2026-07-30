// E2E smoke config (Task #55). Runs against a built app served at E2E_BASE_URL, backed by a real,
// seeded Caisson.Api + Postgres/Redis stack. That stack (seeding a rack, driving a discovery job to
// produce a live snapshot-updated event) is deliberately NOT built here — the harness lives in the
// separate mcp-tooling-caisson repo (tools/live_updates.py, e2e/TopologyLiveUpdatesRunner already
// follow this pattern); this repo owns only the spec and its CI job.
//
// Task #135 (ADR 0040): also home to topology-visual.spec.ts's screenshot baselines, run against the
// same dev-harness route (never E2E_BASE_URL/a live backend) — small, cropped, per-locator snapshots
// (never full-page) so the committed baseline PNGs stay small and diffs stay meaningful. `workers: 1`
// (already required by `fullyParallel: false` above) makes screenshot runs deterministic; baselines are
// generated from — and MUST be regenerated from — the pinned CI/mcp-tooling Linux Chromium build, never
// a local machine's browser (different font rendering/subpixel AA fails the pixel-ratio threshold
// below even with an otherwise-identical DOM).
//
// Story #123 Task #142: `mobile`/`tablet` projects add sm/md viewport coverage for the *-visual specs
// only (`testMatch`, below) — every other spec (harness/smoke/a11y) still runs exactly once, under
// `chromium`, exactly as before. Both projects still spread `devices['Desktop Chrome']` (Chromium, no
// touch emulation) rather than a `devices['iPhone ...']`/`devices['iPad ...']` preset: those pull in
// WebKit + touch-emulation, doubling the baseline-maintenance surface per engine for no real coverage
// gain here (the D3 touch-interaction path is covered separately, in a dedicated real-touch test — see
// ADR 0044) — one browser engine stays consistent with this file's pinned-CI-browser rationale above.
import { defineConfig, devices } from '@playwright/test';

const VISUAL_SPEC_PATTERN = /.*-visual\.spec\.ts$/;

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  workers: 1,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  // Baseline files live alongside the spec under e2e/__screenshots__/<spec-file>/<name>-<platform>.png
  // — a stable, browsable location distinct from Playwright's default (which nests under testDir using
  // the full spec path, harder to scan for the small, curated baseline set this story commits).
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFilePath}/{arg}-{platform}{ext}',
  expect: {
    toHaveScreenshot: {
      // A small non-zero tolerance absorbs anti-aliasing/sub-pixel font-rendering noise between CI
      // runs on the same pinned browser/platform, without masking a genuine visual regression — any
      // real re-skin change (a moved element, a swapped colour) affects far more than 1% of a tightly
      // cropped, per-locator screenshot.
      maxDiffPixelRatio: 0.01,
    },
  },
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    // ~390x844 — representative `sm` (<=640px) viewport.
    {
      name: 'mobile',
      testMatch: VISUAL_SPEC_PATTERN,
      use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 } },
    },
    // ~767x1024 — representative `md` (641-768px) viewport. 767, not 768: `cds-respond-below(md)`
    // (_cds-mixins.scss) triggers strictly BELOW the `md` breakpoint's own 768px min-width — at exactly
    // 768px the desktop/static-sidebar layout has already resumed, so 768 would exercise none of the
    // md-scoped responsive rules this project exists to cover.
    {
      name: 'tablet',
      testMatch: VISUAL_SPEC_PATTERN,
      use: { ...devices['Desktop Chrome'], viewport: { width: 767, height: 1024 } },
    },
  ],
});
