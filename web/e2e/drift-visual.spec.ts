// Visual regression baselines for the Caisson-DS drift/apply re-skin (Story #122, Task #139) —
// mirrors web/e2e/topology-visual.spec.ts exactly (ADR 0040's mechanism, extended per the ADR this
// story records for the drift/apply baselines). Runs against the same deterministic, fully-offline
// dev-harness routes drift-harness.spec.ts already exercises (`/__dev-harness__/drift/:rackId[...]`) —
// never a live backend or seeded data. Every screenshot is a small, bounded, per-locator crop (never a
// full page) so committed PNGs stay small and diffs stay meaningful; timestamps rendered from the
// harness's fixed fixture date are masked where an existing class hook makes that possible, matching
// ADR 0040's caution around locale/timezone formatting drift between runs on the pinned CI browser.
//
// Story #123 Task #142 (ADR 0044): this spec now also runs under the `mobile`/`tablet` viewport projects
// (playwright.config.ts), giving sm/md coverage on top of the desktop-only baselines above. `viewportSuffix`
// keeps the `chromium` project's filenames byte-identical to before this story (empty suffix) so existing
// desktop baselines are never touched, while mobile/tablet runs land in new, distinctly-named files.
import { expect, test } from '@playwright/test';
import type { Locator, Page, TestInfo } from '@playwright/test';

const RACK_ID = 'rack-1';
const DRIFT_ITEM_ID = 'harness-drift-item-1';
const JOB_ID = 'harness-drift-job-1';
const APPLY_ROLES_QUERY = 'roles=ReadOnly,DriftApply';

const LIST_URL = `/__dev-harness__/drift/${RACK_ID}`;
const DETAIL_URL = `/__dev-harness__/drift/${RACK_ID}/items/${DRIFT_ITEM_ID}`;
const AUDIT_URL = `/__dev-harness__/drift/${RACK_ID}/jobs/${JOB_ID}`;

const THEMES = ['dark', 'light', 'hc-dark'] as const;
type Theme = (typeof THEMES)[number];

function viewportSuffix(testInfo: TestInfo): string {
  return testInfo.project.name === 'chromium' ? '' : `-${testInfo.project.name}`;
}

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
      test('drift reports list panel', async ({ page }, testInfo) => {
        await gotoThemed(page, theme, LIST_URL);
        const panel = page.locator('.drift-list');
        await expect(panel).toBeVisible();
        await expect(panel.locator('.drift-list__table')).toBeVisible();
        await expect(panel).toHaveScreenshot(`drift-list-${theme}${viewportSuffix(testInfo)}.png`);
      });

      // Story #123 Task #142: net-new state — `.drift-list__filters` stacks to a single column below
      // `md` (drift-reports-list.component.scss, Story #122) but no existing test isolated just the
      // filter bar; the list-panel test above already captures the whole page, this one captures the
      // stacking specifically.
      test('drift filters stacked', async ({ page }, testInfo) => {
        test.skip(testInfo.project.name === 'chromium', 'filters only stack below `md`');
        await gotoThemed(page, theme, LIST_URL);
        const filters = page.locator('.drift-list__filters');
        await expect(filters).toBeVisible();
        await expect(filters).toHaveScreenshot(
          `drift-filters-stacked-${theme}${viewportSuffix(testInfo)}.png`,
        );
      });

      test('drift report detail view', async ({ page }, testInfo) => {
        await gotoThemed(page, theme, DETAIL_URL);
        const detail = page.locator('.drift-detail');
        await expect(detail).toBeVisible();
        await expect(detail).toHaveScreenshot(
          `drift-detail-${theme}${viewportSuffix(testInfo)}.png`,
          {
            mask: timestampMasks(page),
          },
        );
      });

      // Story #123 Task #142: below `sm` the dialog's actions go `column-reverse` with full-width, 44px
      // buttons (apply-confirmation-dialog.component.scss) — same locator as always, so re-running this
      // test body under the `mobile`/`tablet` projects already captures that state under its own
      // viewport-suffixed filename; no separate test needed.
      test('apply confirmation dialog', async ({ page }, testInfo) => {
        await gotoThemed(page, theme, `${DETAIL_URL}?${APPLY_ROLES_QUERY}`);
        await page.locator('.apply-action__apply').click();
        const dialog = page.locator('.apply-dialog');
        await expect(dialog).toBeVisible();
        await expect(dialog).toHaveScreenshot(
          `apply-dialog-${theme}${viewportSuffix(testInfo)}.png`,
        );
      });

      test('job status timeline — active stage after a live status update', async ({
        page,
      }, testInfo) => {
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
        await expect(timeline).toHaveScreenshot(
          `job-status-timeline-executing-${theme}${viewportSuffix(testInfo)}.png`,
        );
      });

      test('audit record view', async ({ page }, testInfo) => {
        await gotoThemed(page, theme, AUDIT_URL);
        const audit = page.locator('.audit-view');
        await expect(audit).toBeVisible();
        await expect(audit).toHaveScreenshot(`audit-view-${theme}${viewportSuffix(testInfo)}.png`, {
          mask: timestampMasks(page),
        });
      });
    });
  }
});
