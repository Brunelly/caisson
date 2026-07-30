// Visual regression baselines for the Caisson-DS topology re-skin (Story #121, Task #135, ADR 0040 —
// resolved Q&A: screenshot tests now, against the deterministic dev-harness route, rather than a manual
// checklist or a full Storybook setup). Runs against `/__dev-harness__/topology/rack-1` — the same
// fixture-backed, fully-offline harness topology-harness.spec.ts/theme-shell.spec.ts already use — so
// baselines never depend on a live backend or seeded data. Every screenshot is a small, bounded,
// per-locator crop (never a full page) so committed PNGs stay small and diffs stay meaningful; any
// rendered timestamp is masked so a formatting/timezone difference between CI runs never fails a pixel
// diff that isn't a real visual regression.
import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

const HARNESS_URL = '/__dev-harness__/topology/rack-1';
const THEMES = ['dark', 'light', 'hc-dark'] as const;
type Theme = (typeof THEMES)[number];

async function gotoHarnessThemed(
  page: Page,
  theme: Theme,
  query: Record<string, string> = {},
): Promise<void> {
  // Matches theme-shell.spec.ts's pattern: seed the persisted preference via addInitScript BEFORE
  // navigation, so ThemeService resolves it deterministically instead of depending on the test
  // environment's emulated colour-scheme default.
  await page.addInitScript((t) => localStorage.setItem('caisson.theme', t), theme);
  const params = new URLSearchParams(query);
  const url = params.toString() ? `${HARNESS_URL}?${params.toString()}` : HARNESS_URL;
  await page.goto(url);
  await expect(page.locator('svg.topology-graph')).toBeVisible();
  // Let the D3 zoom/glass transitions/animations that fire on first paint settle before the screenshot.
  await page.waitForTimeout(150);
}

function timestampMasks(page: Page): Locator[] {
  return [page.locator('.snapshot-meta'), page.locator('.djs-widget__meta')];
}

test.describe('Topology re-skin — visual regression baselines (Task #135, ADR 0040)', () => {
  for (const theme of THEMES) {
    test.describe(`theme: ${theme}`, () => {
      test('default graph view', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        await expect(page.locator('svg.topology-graph')).toHaveScreenshot(
          `graph-default-${theme}.png`,
          { mask: timestampMasks(page) },
        );
      });

      test('selected node shows the DS glow/ring highlight', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        await page.locator('g.node--confirmed[aria-label*="eth0"]').click();
        await expect(page.locator('svg.topology-graph')).toHaveScreenshot(
          `graph-selected-node-${theme}.png`,
          { mask: timestampMasks(page) },
        );
      });

      test('details panel open', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        await page.locator('g.node--ambiguous[aria-label*="eth1"]').click();
        const panel = page.locator('aside.details-panel');
        await expect(panel).toBeVisible();
        await expect(panel).toHaveScreenshot(`details-panel-${theme}.png`, {
          mask: timestampMasks(page).concat(page.locator('.details-panel__snapshot-meta')),
        });
      });

      test('search dropdown open', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        const searchInput = page.getByRole('combobox', { name: /search topology/i });
        await searchInput.click();
        await searchInput.fill('e');
        const results = page.locator('.topology-search__results');
        await expect(results).toBeVisible();
        await expect(results).toHaveScreenshot(`search-dropdown-${theme}.png`);
      });

      test('legend', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        const legend = page.getByRole('region', { name: 'Topology graph legend' });
        await expect(legend).toHaveScreenshot(`legend-${theme}.png`);
      });

      for (const variant of ['succeeded', 'inProgress', 'failed'] as const) {
        test(`discovery-status widget — ${variant}`, async ({ page }) => {
          await gotoHarnessThemed(page, theme, { discoveryStatus: variant });
          const widget = page.locator('.djs-widget');
          await expect(widget).toBeVisible();
          await expect(widget).toHaveScreenshot(`discovery-widget-${variant}-${theme}.png`, {
            mask: timestampMasks(page),
          });
        });
      }

      test('stale/disconnected live-update banner', async ({ page }) => {
        await gotoHarnessThemed(page, theme);
        await page.evaluate(() => window.__harness__!.hub.simulateReconnecting());
        const banner = page.locator('.lcsb-banner');
        await expect(banner).toBeVisible();
        await expect(banner).toHaveScreenshot(`connection-banner-disconnected-${theme}.png`);
      });
    });
  }
});
