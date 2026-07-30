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
import { defineConfig, devices } from '@playwright/test';

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
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
