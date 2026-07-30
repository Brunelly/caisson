// Real-browser coverage for the Caisson Design System theming shell (Story #119 Task #128), run
// against the dev-only harness route (`/__dev-harness__/topology/:rackId`, see
// web/src/app/dev-harness/) — the same harness topology-harness.spec.ts/drift-harness.spec.ts use, so
// the shell chrome (sidebar, top bar, theme toggle) rendered here is the real production code, not a
// mock. Complements app-shell.a11y.spec.ts (jsdom axe pass, which disables color-contrast since jsdom
// cannot paint) — THIS spec is the only real gate for the AA/glass/scrim requirement (AC4) and for the
// FOUC/persistence/system-preference requirements (NFR1, AC2, AC3) that need a real browser.
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  TOPOLOGY_HARNESS_URL as HARNESS_URL,
  captureConsoleErrors,
  gotoTopologyHarness as gotoHarness,
  themeToggle,
} from './harness-helpers';

const STORAGE_KEY = 'caisson.theme';

/** Shared by the desktop-viewport test below and its `sm`/`md` re-verification (Story #123 Task #143) —
 * switches through all three themes via the toggle (still directly in the top bar, not moved behind the
 * mobile nav drawer, at any viewport) and asserts zero axe violations, including real-browser colour
 * contrast, at each. */
async function expectAllThemesAA(page: Page): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('caisson.theme', 'dark'));
  await gotoHarness(page);
  const toggle = themeToggle(page);

  const darkResults = await new AxeBuilder({ page })
    .options({ rules: { region: { enabled: false } } })
    .analyze();
  expect(darkResults.violations).toEqual([]);

  await toggle.getByRole('radio', { name: 'Light' }).click();
  await expect(toggle.getByRole('radio', { name: 'Light' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  // Move the pointer off the toggle so a lingering :hover state (from the click above) isn't scanned
  // as the resting state — this is a real-browser artifact of automated clicking, not part of the theme.
  await page.mouse.move(0, 0);
  const lightResults = await new AxeBuilder({ page })
    .options({ rules: { region: { enabled: false } } })
    .analyze();
  expect(lightResults.violations).toEqual([]);

  await toggle.getByRole('radio', { name: 'High contrast' }).click();
  await expect(toggle.getByRole('radio', { name: 'High contrast' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await page.mouse.move(0, 0);
  const hcResults = await new AxeBuilder({ page })
    .options({ rules: { region: { enabled: false } } })
    .analyze();
  expect(hcResults.violations).toEqual([]);
}

test.describe('Theming shell — dev harness (real browser)', () => {
  test('toggling a theme updates data-theme + localStorage immediately, with no page reload', async ({
    page,
  }) => {
    await gotoHarness(page);
    const toggle = themeToggle(page);

    await toggle.getByRole('radio', { name: 'Light' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    await expect
      .poll(() => page.evaluate((key) => localStorage.getItem(key), STORAGE_KEY))
      .toBe('light');

    await toggle.getByRole('radio', { name: 'High contrast' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'hc-dark');
    await expect
      .poll(() => page.evaluate((key) => localStorage.getItem(key), STORAGE_KEY))
      .toBe('hc-dark');

    await toggle.getByRole('radio', { name: 'Dark' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await expect
      .poll(() => page.evaluate((key) => localStorage.getItem(key), STORAGE_KEY))
      .toBe('dark');

    // Still on the same page (no reload/navigation triggered by a theme switch — AC2/AC5).
    expect(page.url()).toContain(HARNESS_URL);
  });

  test('a selected theme survives page.reload() (AC3: persisted preference restored)', async ({
    page,
  }) => {
    await gotoHarness(page);
    await themeToggle(page).getByRole('radio', { name: 'High contrast' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'hc-dark');

    await page.reload();
    await expect(page.locator('svg.topology-graph')).toBeVisible();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'hc-dark');
    await expect(themeToggle(page).getByRole('radio', { name: 'High contrast' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  test('first load with no stored preference honours prefers-color-scheme (AC3)', async ({
    page,
  }) => {
    await page.context().clearCookies();
    await page.addInitScript(() => localStorage.removeItem('caisson.theme'));

    await page.emulateMedia({ colorScheme: 'light' });
    await gotoHarness(page);
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    await page.evaluate((key) => localStorage.removeItem(key), STORAGE_KEY);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.reload();
    await expect(page.locator('svg.topology-graph')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  });

  test('theme toggle is fully keyboard-operable: Tab reaches it, arrow keys move + select, Enter/Space select', async ({
    page,
  }) => {
    // Deterministic starting point — a fresh context's emulated colour scheme isn't guaranteed to be
    // "dark", so seed the persisted preference explicitly rather than assume the system default.
    await page.addInitScript(() => localStorage.setItem('caisson.theme', 'dark'));
    await gotoHarness(page);

    const darkRadio = themeToggle(page).getByRole('radio', { name: 'Dark' });
    const lightRadio = themeToggle(page).getByRole('radio', { name: 'Light' });
    const hcRadio = themeToggle(page).getByRole('radio', { name: 'High contrast' });

    await expect(darkRadio).toHaveAttribute('aria-checked', 'true');
    await darkRadio.focus();

    await page.keyboard.press('ArrowRight');
    await expect(lightRadio).toBeFocused();
    await expect(lightRadio).toHaveAttribute('aria-checked', 'true');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    await page.keyboard.press('ArrowRight');
    await expect(hcRadio).toBeFocused();
    await expect(hcRadio).toHaveAttribute('aria-checked', 'true');

    await page.keyboard.press('ArrowLeft');
    await expect(lightRadio).toBeFocused();
    await expect(lightRadio).toHaveAttribute('aria-checked', 'true');

    // Enter/Space activation (native <button> semantics) on the focused, unchecked "Dark" option.
    await page.keyboard.press('ArrowLeft');
    await expect(darkRadio).toBeFocused();
    await expect(darkRadio).toHaveAttribute('aria-checked', 'true');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  });

  test('no new console errors are logged while switching themes (AC5)', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await gotoHarness(page);
    const toggle = themeToggle(page);
    await toggle.getByRole('radio', { name: 'Light' }).click();
    await toggle.getByRole('radio', { name: 'High contrast' }).click();
    await toggle.getByRole('radio', { name: 'Dark' }).click();

    expect(errors).toEqual([]);
  });

  test('has no automatically-detectable accessibility violations, including real-browser color contrast, across all three themes', async ({
    page,
  }) => {
    await expectAllThemesAA(page);
  });

  // Story #123 Task #143: re-verifies theme switching stays WCAG AA at `sm`/`md` under the re-skinned,
  // now-collapsible shell — the theme toggle is never moved behind the mobile nav drawer, so
  // `expectAllThemesAA` needs no changes to run unmodified at these viewports.
  test.describe('responsive (sm/md)', () => {
    for (const viewport of [
      { name: 'sm', width: 390, height: 844 },
      { name: 'md', width: 767, height: 1024 },
    ] as const) {
      test(`stays WCAG AA across all three themes at ${viewport.name}`, async ({ page }) => {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await expectAllThemesAA(page);
      });

      // AC5/NFR5: no new console errors/warnings, extended to the mobile-viewport runs (mirrors the
      // desktop-only "no new console errors" test above, plus the drawer interaction this story added).
      test(`no new console errors opening/closing the nav drawer and switching themes at ${viewport.name}`, async ({
        page,
      }) => {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        const errors = captureConsoleErrors(page);

        await gotoHarness(page);
        const hamburger = page.getByRole('button', { name: 'Open navigation' });
        await hamburger.click();
        await expect(page.locator('.nav-drawer')).toBeVisible();
        await page.keyboard.press('Escape');
        await expect(page.locator('.nav-drawer')).toBeHidden();

        const toggle = themeToggle(page);
        await toggle.getByRole('radio', { name: 'Light' }).click();
        await toggle.getByRole('radio', { name: 'High contrast' }).click();
        await toggle.getByRole('radio', { name: 'Dark' }).click();

        expect(errors).toEqual([]);
      });
    }
  });
});
