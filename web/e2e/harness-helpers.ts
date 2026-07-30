// Story #123 Task #143: shared navigation/assertion helpers, extracted once `theme-shell.spec.ts` and
// `topology-harness.spec.ts` turned out to define byte-identical `HARNESS_URL`/`gotoHarness()` copies
// independently. Kept deliberately small — only genuinely-duplicated helpers move here, not every
// spec-local constant (`drift-harness.spec.ts`'s RACK_ID/DRIFT_ITEM_ID/JOB_ID stay local: they're never
// duplicated elsewhere, and forcing them through a shared module would just add an indirection).
import { expect } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

export const TOPOLOGY_HARNESS_URL = '/__dev-harness__/topology/rack-1';

/** Navigates to the topology dev-harness route and waits for the graph to render — the common
 * precondition every topology-harness/theme-shell test starts from. */
export async function gotoTopologyHarness(page: Page): Promise<void> {
  await page.goto(TOPOLOGY_HARNESS_URL);
  await expect(page.locator('svg.topology-graph')).toBeVisible();
}

export function themeToggle(page: Page): Locator {
  return page.getByRole('radiogroup', { name: 'Theme' });
}

/** NFR1: no page-level horizontal scroll at sm/md — individual components (the drift table, the search
 * results overlay) may still scroll horizontally within themselves; this only asserts the outermost
 * document never does. */
export async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflows = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  );
  expect(overflows).toBe(false);
}

/** NFR3: every selector's FIRST matching element reports a real >=44 CSS px hit target. Anchored on the
 * `cds-touch-target` mixin (`_cds-mixins.scss`) shell/topology/drift controls were migrated onto — a
 * regression here means either that mixin's floor regressed or a control stopped using it. Elements
 * that don't exist in the current view (e.g. a drift row link on a page with no drift items) are
 * skipped rather than failed, since their absence is a separate, more specific test's concern. */
export async function expectTouchTargets(page: Page, selectors: readonly string[]): Promise<void> {
  for (const selector of selectors) {
    const locator = page.locator(selector).first();
    if ((await locator.count()) === 0) {
      continue;
    }
    const box = await locator.boundingBox();
    expect(box, `${selector} has no bounding box (not visible?)`).not.toBeNull();
    expect(box!.height, `${selector} height`).toBeGreaterThanOrEqual(44);
    expect(box!.width, `${selector} width`).toBeGreaterThanOrEqual(44);
  }
}

/** AC5/NFR5: captures console errors/pageerrors from the moment this is called — call BEFORE the
 * navigation/interaction under test, then assert `errors` is empty afterwards (mirrors the existing
 * inline pattern theme-shell.spec.ts's own console test already used, generalised for reuse). */
export function captureConsoleErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      errors.push(message.text());
    }
  });
  page.on('pageerror', (error) => errors.push(error.message));
  return errors;
}
