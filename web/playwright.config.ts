// E2E smoke config (Task #55). Runs against a built app served at E2E_BASE_URL, backed by a real,
// seeded Caisson.Api + Postgres/Redis stack. That stack (seeding a rack, driving a discovery job to
// produce a live snapshot-updated event) is deliberately NOT built here — the harness lives in the
// separate mcp-tooling-caisson repo (tools/live_updates.py, e2e/TopologyLiveUpdatesRunner already
// follow this pattern); this repo owns only the spec and its CI job.
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  workers: 1,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
