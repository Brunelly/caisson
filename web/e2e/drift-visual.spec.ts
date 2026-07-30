// Visual regression baselines for the Caisson-DS drift/apply re-skin (Story #122, Task #139) —
// mirrors web/e2e/topology-visual.spec.ts exactly (ADR 0040's mechanism, extended per the ADR this
// story records for the drift/apply baselines). Runs against the same deterministic, fully-offline
// dev-harness routes drift-harness.spec.ts already exercises (`/__dev-harness__/drift/:rackId[...]`) —
// never a live backend or seeded data. Every screenshot is a small, bounded, per-locator crop (never a
// full page) so committed PNGs stay small and diffs stay meaningful; timestamps rendered from the
// harness's fixed fixture date are masked where an existing class hook makes that possible, matching
// ADR 0040's caution around locale/timezone formatting drift between runs on the pinned CI browser.
import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

const RACK_ID = 'rack-1';
const DRIFT_ITEM_ID = 'harness-drift-item-1';
const JOB_ID = 'harness-drift-job-1';
const APPLY_ROLES_QUERY = 'roles=ReadOnly,DriftApply';

const LIST_URL = `/__dev-harness__/drift/${RACK_ID}`;
const DETAIL_URL = `/__dev-harness__/drift/${RACK_ID}/items/${DRIFT_ITEM_ID}`;
const AUDIT_URL = `/__dev-harness__/drift/${RACK_ID}/jobs/${JOB_ID}`;

const THEMES = ['dark', 'light', 'hc-dark'] as const;
type Theme = (typeof THEMES)[number];

async function gotoThemed(page: Page, theme: Theme, url: string): Promise<void> {
  // Matches theme-shell.spec.ts/topology-visual.spec.ts's pattern: seed the persisted preference via
  // addInitScript BEFORE navigation, so ThemeService resolves it deterministically.
  await page.addInitScript((t) => localStorage.setItem('caisson.theme', t), theme);
  await page.goto(url);
}

function timestampMasks(page: Page): Locator[] {
  return [page.locator('.drift-detail__detected'), page.locator('.audit-view__trail-date')];
}

test.describe('Drift/apply re-skin — visual regression baselines (Task #139)', () => {
  for (const theme of THEMES) {
    test.describe(`theme: ${theme}`, () => {
      test('drift reports list panel', async ({ page }) => {
        await gotoThemed(page, theme, LIST_URL);
        const panel = page.locator('.drift-list');
        await expect(panel).toBeVisible();
        await expect(panel.locator('.drift-list__table')).toBeVisible();
        await expect(panel).toHaveScreenshot(`drift-list-${theme}.png`);
      });

      test('drift report detail view', async ({ page }) => {
        await gotoThemed(page, theme, DETAIL_URL);
        const detail = page.locator('.drift-detail');
        await expect(detail).toBeVisible();
        await expect(detail).toHaveScreenshot(`drift-detail-${theme}.png`, {
          mask: timestampMasks(page),
        });
      });

      test('apply confirmation dialog', async ({ page }) => {
        await gotoThemed(page, theme, `${DETAIL_URL}?${APPLY_ROLES_QUERY}`);
        await page.locator('.apply-action__apply').click();
        const dialog = page.locator('.apply-dialog');
        await expect(dialog).toBeVisible();
        await expect(dialog).toHaveScreenshot(`apply-dialog-${theme}.png`);
      });

      test('job status timeline — active stage after a live status update', async ({ page }) => {
        await gotoThemed(page, theme, `${DETAIL_URL}?${APPLY_ROLES_QUERY}`);
        await page.locator('.apply-action__apply').click();
        const dialog = page.locator('.apply-dialog');
        await dialog.locator('input[type="checkbox"]').check();
        await dialog.locator('.apply-dialog__submit').click();
        await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 2000 });

        await page.evaluate((jobId) => {
          window.__harness__!.hub.simulateDriftApplyJobStatusChanged({
            rackId: 'rack-1',
            jobId,
            status: 'Executing',
            previousStatus: 'Revalidating',
            currentStep: 'DeviceApply',
            reasonCode: null,
            errorCode: null,
            timestamp: new Date(2026, 0, 1, 12, 0, 5).toISOString(),
            seq: 1,
            correlationId: 'corr-visual-1',
          });
        }, JOB_ID);
        await expect(page.locator('app-job-status-badge')).toContainText('Executing');

        const timeline = page.locator('app-job-status-timeline');
        await expect(timeline).toHaveScreenshot(`job-status-timeline-executing-${theme}.png`);
      });

      test('audit record view', async ({ page }) => {
        await gotoThemed(page, theme, AUDIT_URL);
        const audit = page.locator('.audit-view');
        await expect(audit).toBeVisible();
        await expect(audit).toHaveScreenshot(`audit-view-${theme}.png`, {
          mask: timestampMasks(page),
        });
      });
    });
  }
});
