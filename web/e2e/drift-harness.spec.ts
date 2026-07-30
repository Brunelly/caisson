// Real-browser interaction/accessibility coverage for the drift + apply surface (story #67), run
// against the dev-only harness routes (`/__dev-harness__/drift/:rackId[...]`, see src/app/dev-harness/)
// instead of the real OIDC/Entra-gated routes — mirrors web/e2e/topology-harness.spec.ts's approach:
// fakes only the wire (HTTP services + the SignalR hub connection), so every component under test
// (list, filters, detail, confirmation dialog, live status, audit view) is the real production code.
//
// The RBAC claim is parameterised via a `?roles=` query param the harness's fake OidcSecurityService
// reads at call time (see dev-harness.providers.ts) — RBAC-hidden (no DriftApply) is the default; tests
// exercising the Apply workflow navigate with `?roles=ReadOnly,DriftApply`.
//
// Cross-page navigation between the harness's list/detail/audit routes uses page.goto() directly rather
// than clicking the components' own routerLinks: those links point at the real production paths
// (`/racks/:rackId/drift/...`), which in a dev build resolve to the REAL roleGuard-protected routes, not
// the harness ones — the harness route prefix necessarily differs. This still exercises every real
// component in a real browser; only the *inter-page click* is replaced with a direct navigation. The
// link hrefs themselves are asserted once to prove the wiring is correct.
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  captureConsoleErrors,
  expectNoHorizontalScroll,
  expectTouchTargets,
} from './harness-helpers';

const RACK_ID = 'rack-1';
const DRIFT_ITEM_ID = 'harness-drift-item-1';
const JOB_ID = 'harness-drift-job-1';
const APPLY_ROLES_QUERY = '?roles=ReadOnly,DriftApply';

const LIST_URL = `/__dev-harness__/drift/${RACK_ID}`;
const DETAIL_URL = `/__dev-harness__/drift/${RACK_ID}/items/${DRIFT_ITEM_ID}`;
const AUDIT_URL = `/__dev-harness__/drift/${RACK_ID}/jobs/${JOB_ID}`;

async function gotoList(page: Page, withApplyPermission = false): Promise<void> {
  await page.goto(withApplyPermission ? `${LIST_URL}${APPLY_ROLES_QUERY}` : LIST_URL);
  await expect(page.locator('.drift-list')).toBeVisible();
}

async function gotoDetail(page: Page, withApplyPermission = false): Promise<void> {
  await page.goto(withApplyPermission ? `${DETAIL_URL}${APPLY_ROLES_QUERY}` : DETAIL_URL);
  await expect(page.locator('.drift-detail')).toBeVisible();
}

test.describe('Drift page — dev harness (real browser)', () => {
  test('list renders AC2 columns and links to the detail view', async ({ page }) => {
    await gotoList(page);

    const table = page.locator('.drift-list__table');
    await expect(table).toBeVisible();
    await expect(table.getByRole('cell', { name: 'AccessVlanMismatch' })).toBeVisible();
    await expect(page.getByText(/SwitchPort: v1\|rack-1\|SW-1\|ether2/)).toBeVisible();

    const link = page.locator('.drift-list__subject-link');
    await expect(link).toHaveAttribute('href', `/racks/${RACK_ID}/drift/items/${DRIFT_ITEM_ID}`);
  });

  test('filters re-drive the list server-side via query params, surviving as URL state', async ({
    page,
  }) => {
    await gotoList(page);

    const table = page.locator('.drift-list__table');
    await page.getByLabel('Severity').selectOption('High');
    await expect(page).toHaveURL(/severity=High/);
    await expect(table.getByRole('cell', { name: 'AccessVlanMismatch' })).toBeVisible();

    // The single fixture item is High severity — filtering to Low leaves the table empty.
    await page.getByLabel('Severity').selectOption('Low');
    await expect(page).toHaveURL(/severity=Low/);
    await expect(
      page.getByRole('status').filter({ hasText: 'No drift items match' }),
    ).toBeVisible();

    // Back/forward preserves filter state via the URL (query-param-bound filters).
    await page.goBack();
    await expect(page).toHaveURL(/severity=High/);
    await expect(table.getByRole('cell', { name: 'AccessVlanMismatch' })).toBeVisible();
  });

  test('detail view renders why, drift type, severity, and before/after', async ({ page }) => {
    await gotoDetail(page);

    await expect(page.locator('.drift-detail__why')).toContainText('Access VLAN mismatch');
    await expect(page.locator('.drift-detail__before-after')).toContainText('100');
    await expect(page.locator('.drift-detail__before-after')).toContainText('200');
    await expect(page.locator('app-drift-severity-badge')).toContainText('High severity');
  });

  test('RBAC-hidden: no DriftApply claim renders no Apply button, only an explanation naming the permission', async ({
    page,
  }) => {
    await gotoDetail(page, false);

    await expect(page.locator('.apply-action__apply')).toHaveCount(0);
    await expect(page.locator('.apply-action__no-permission')).toContainText('DriftApply');
  });

  test('Apply workflow: confirm dialog ack gate, Cancel makes zero calls, then submit and reach a live-status Completed outcome via a simulated hub event', async ({
    page,
  }) => {
    await gotoDetail(page, true);

    const applyButton = page.locator('.apply-action__apply');
    await expect(applyButton).toBeVisible();

    // Cancel first: zero API calls, dialog closes, focus returns to the trigger.
    await applyButton.click();
    const dialog = page.locator('.apply-dialog');
    await expect(dialog).toBeVisible();
    await dialog.locator('.apply-dialog__cancel').click();
    await expect(dialog).toBeHidden();
    await expect(applyButton).toBeFocused();
    expect(await page.evaluate(() => window.__harness__!.getApplyCorrectionCallCount())).toBe(0);

    // Now actually submit: Submit stays disabled until the acknowledgement checkbox is ticked.
    await applyButton.click();
    await expect(dialog).toBeVisible();
    const submit = dialog.locator('.apply-dialog__submit');
    await expect(submit).toBeDisabled();
    await dialog.locator('input[type="checkbox"]').check();
    await expect(submit).toBeEnabled();
    await submit.click();
    await expect(dialog).toBeHidden();

    await expect(page.locator('.apply-action__job')).toBeVisible();
    await expect(page.locator('app-job-status-badge')).toContainText('Pending', { timeout: 2000 });

    // Live status: the fake hub pushes Executing then Completed on the SAME TopologyHub connection.
    await page.evaluate(() => {
      window.__harness__!.hub.simulateDriftApplyJobStatusChanged({
        rackId: 'rack-1',
        jobId: 'harness-drift-job-1',
        status: 'Executing',
        previousStatus: 'Pending',
        currentStep: 'DeviceApply',
        reasonCode: null,
        errorCode: null,
        timestamp: new Date().toISOString(),
        seq: 1,
        correlationId: 'corr-live-1',
      });
    });
    await expect(page.locator('app-job-status-badge')).toContainText('Executing');

    await page.evaluate(() => {
      window.__harness__!.hub.simulateDriftApplyJobStatusChanged({
        rackId: 'rack-1',
        jobId: 'harness-drift-job-1',
        status: 'Completed',
        previousStatus: 'Executing',
        currentStep: null,
        reasonCode: null,
        errorCode: null,
        timestamp: new Date().toISOString(),
        seq: 2,
        correlationId: 'corr-live-2',
      });
    });
    await expect(page.locator('app-job-status-badge')).toContainText('Completed');
    await expect(page.locator('.apply-action__outcome')).toContainText('Success');

    // Older/duplicate seq events are ignored (idempotency) — status stays Completed.
    await page.evaluate(() => {
      window.__harness__!.hub.simulateDriftApplyJobStatusChanged({
        rackId: 'rack-1',
        jobId: 'harness-drift-job-1',
        status: 'Executing',
        previousStatus: 'Pending',
        currentStep: 'DeviceApply',
        reasonCode: null,
        errorCode: null,
        timestamp: new Date().toISOString(),
        seq: 1, // stale — already superseded by seq 2
        correlationId: 'corr-live-stale',
      });
    });
    await expect(page.locator('app-job-status-badge')).toContainText('Completed');

    // The job id links to the stable audit URL.
    await expect(page.locator('.apply-action__job-link')).toHaveAttribute(
      'href',
      `/racks/${RACK_ID}/drift/jobs/${JOB_ID}`,
    );
  });

  test('double-submit guard: a rapid second click while the apply call is in flight fires exactly one applyCorrection call', async ({
    page,
  }) => {
    await gotoDetail(page, true);

    await page.locator('.apply-action__apply').click();
    const dialog = page.locator('.apply-dialog');
    await dialog.locator('input[type="checkbox"]').check();
    await dialog.locator('.apply-dialog__submit').click();
    await expect(dialog).toBeHidden();

    // The harness's applyCorrection has an artificial ~400ms delay (see dev-harness.providers.ts) so
    // this window is real: the outer Apply button must already be disabled ("Applying…") while in
    // flight, and clicking it again must not fire a second call.
    const applyButton = page.locator('.apply-action__apply');
    await expect(applyButton).toBeDisabled();
    await expect(applyButton).toContainText('Applying');
    await applyButton.click({ force: true });

    await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 3000 });
    expect(await page.evaluate(() => window.__harness__!.getApplyCorrectionCallCount())).toBe(1);
  });

  test('polling fallback: on hub drop with an active job, live status still reaches a terminal outcome via polling', async ({
    page,
  }) => {
    await gotoDetail(page, true);

    await page.locator('.apply-action__apply').click();
    const dialog = page.locator('.apply-dialog');
    await dialog.locator('input[type="checkbox"]').check();
    await dialog.locator('.apply-dialog__submit').click();
    await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 2000 });

    // Drop the hub — the existing stale/disconnected banner (reused, not a second state machine; now
    // rendered by the shared LiveConnectionStatusBarComponent, Story #119) shows.
    await page.evaluate(() => window.__harness__!.hub.simulateClose());
    await expect(page.locator('.lcsb-banner--disconnected')).toBeVisible();

    // Advance the harness's job status so the next poll observes a terminal outcome.
    await page.evaluate(() => window.__harness__!.setDriftJobStatus('Completed'));

    // The existing poll cadence (15s) is real; wait for it rather than trying to fast-forward a real
    // browser's timers.
    await expect(page.locator('app-job-status-badge')).toContainText('Completed', {
      timeout: 20_000,
    });
  });

  // Task #131/#132: topology/drift feature screens were migrated onto --cds-* tokens (ADR 0038)
  // specifically so hc-dark resolves a real palette instead of silently falling through to dark-theme
  // colours on the shell's black hc-dark background — these three scans are the real-browser gate that
  // catches it, extended across list/detail+dialog/audit (the drift surfaces this story touched).
  async function scanAllThemes(
    page: Page,
    axeOptions: { rules: Record<string, { enabled: boolean }> },
  ) {
    const lightResults = await new AxeBuilder({ page }).options(axeOptions).analyze();
    expect(lightResults.violations).toEqual([]);

    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    const darkResults = await new AxeBuilder({ page }).options(axeOptions).analyze();
    expect(darkResults.violations).toEqual([]);

    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'hc-dark'));
    const hcDarkResults = await new AxeBuilder({ page }).options(axeOptions).analyze();
    expect(hcDarkResults.violations).toEqual([]);
  }

  test('has no automatically-detectable accessibility violations, including real-browser colour contrast for severity/job-status badges, in light, dark, and high-contrast themes', async ({
    page,
  }) => {
    await gotoDetail(page, true);
    await page.locator('.apply-action__apply').click();
    const dialog = page.locator('.apply-dialog');
    await dialog.locator('input[type="checkbox"]').check();
    await dialog.locator('.apply-dialog__submit').click();
    await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 2000 });

    await scanAllThemes(page, { rules: { region: { enabled: false } } });
  });

  test('drift list has no automatically-detectable accessibility violations, including real-browser colour contrast, in light, dark, and high-contrast themes', async ({
    page,
  }) => {
    await gotoList(page);

    await scanAllThemes(page, { rules: { region: { enabled: false } } });
  });

  test('audit view is read-only and renders actor/target/before-after/outcome from the job detail', async ({
    page,
  }) => {
    await page.goto(AUDIT_URL);

    await expect(page.locator('.audit-view')).toBeVisible();
    await expect(page.locator('.audit-view__fields')).toContainText('harness-user');
    await expect(page.locator('.audit-view__fields')).toContainText('SW-1');
    await expect(page.locator('.audit-view__fields')).toContainText('ether2');
    // Scoped to the audit view's own content, not the whole page — the app-shell chrome (Story #119)
    // legitimately adds its own interactive controls (e.g. the theme toggle) outside this page.
    await expect(page.locator('.audit-view').locator('button, input, form')).toHaveCount(0);
  });

  test('audit view has no automatically-detectable accessibility violations, including real-browser colour contrast, in light, dark, and high-contrast themes', async ({
    page,
  }) => {
    await page.goto(AUDIT_URL);
    await expect(page.locator('.audit-view')).toBeVisible();

    await scanAllThemes(page, { rules: { region: { enabled: false } } });
  });

  // Story #123 Task #143: re-verifies #65-#68's RBAC gate, double-submit guard, and live SignalR status
  // at `sm`/`md` under the re-skinned, now-collapsible shell — and NFR1/NFR3 on the drift surfaces.
  test.describe('responsive (sm/md)', () => {
    for (const viewport of [
      { name: 'sm', width: 390, height: 844 },
      { name: 'md', width: 767, height: 1024 },
    ] as const) {
      test.describe(`viewport: ${viewport.name}`, () => {
        test.use({ viewport: { width: viewport.width, height: viewport.height } });

        test('no page-level horizontal scroll on list/detail (NFR1)', async ({ page }) => {
          await gotoList(page);
          await expectNoHorizontalScroll(page);

          await gotoDetail(page);
          await expectNoHorizontalScroll(page);
        });

        // `.drift-list__subject-link`/`__status-link` are `cds-respond-below(md)` (drift-reports-list
        // .component.scss), so this holds at both sm and md; `.apply-action__apply` is `cds-respond-
        // below(sm)` only (mirroring the dialog's own buttons) — checked separately, at sm, below.
        test('drift row links are >=44px (NFR3)', async ({ page }) => {
          await gotoList(page);
          await expectTouchTargets(page, ['.drift-list__subject-link', '.drift-list__status-link']);
        });

        // AC5's explicit concern: RBAC-hidden Apply must not be revealed via any responsive
        // overflow/drawer affordance this story added — the hamburger drawer only ever renders
        // SidebarNavigationComponent (nav-drawer.component.ts), never drift/apply controls, so there is
        // no code path that could leak it; this pins that down as a regression guard.
        test('RBAC-hidden: Apply stays hidden, and does not leak via the shell nav drawer', async ({
          page,
        }) => {
          await gotoDetail(page, false);
          await expect(page.locator('.apply-action__apply')).toHaveCount(0);

          await page.getByRole('button', { name: 'Open navigation' }).click();
          const drawer = page.locator('.nav-drawer');
          await expect(drawer).toBeVisible();
          await expect(drawer.locator('.apply-action__apply')).toHaveCount(0);
          await expect(drawer.getByRole('button', { name: /apply/i })).toHaveCount(0);
        });

        test('double-submit guard still fires exactly one applyCorrection call', async ({
          page,
        }) => {
          await gotoDetail(page, true);

          await page.locator('.apply-action__apply').click();
          const dialog = page.locator('.apply-dialog');
          await dialog.locator('input[type="checkbox"]').check();
          await dialog.locator('.apply-dialog__submit').click();
          await expect(dialog).toBeHidden();

          const applyButton = page.locator('.apply-action__apply');
          await expect(applyButton).toBeDisabled();
          await applyButton.click({ force: true });
          await applyButton.click({ force: true });

          await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 3000 });
          expect(await page.evaluate(() => window.__harness__!.getApplyCorrectionCallCount())).toBe(
            1,
          );
        });

        // AC5/NFR5: no new console errors/warnings through the full apply workflow at this viewport.
        test('no new console errors through the apply workflow', async ({ page }) => {
          const errors = captureConsoleErrors(page);

          await gotoDetail(page, true);
          await page.locator('.apply-action__apply').click();
          const dialog = page.locator('.apply-dialog');
          await dialog.locator('input[type="checkbox"]').check();
          await dialog.locator('.apply-dialog__submit').click();
          await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 2000 });

          expect(errors).toEqual([]);
        });

        test('live SignalR job status still patches in place, no reload', async ({ page }) => {
          await gotoDetail(page, true);
          const urlBefore = page.url();

          await page.locator('.apply-action__apply').click();
          const dialog = page.locator('.apply-dialog');
          await dialog.locator('input[type="checkbox"]').check();
          await dialog.locator('.apply-dialog__submit').click();
          await expect(page.locator('.apply-action__job')).toBeVisible({ timeout: 2000 });

          await page.evaluate(() => {
            window.__harness__!.hub.simulateDriftApplyJobStatusChanged({
              rackId: 'rack-1',
              jobId: 'harness-drift-job-1',
              status: 'Completed',
              previousStatus: 'Pending',
              currentStep: null,
              reasonCode: null,
              errorCode: null,
              timestamp: new Date().toISOString(),
              seq: 1,
              correlationId: 'corr-responsive-1',
            });
          });
          await expect(page.locator('app-job-status-badge')).toContainText('Completed');
          expect(page.url()).toBe(urlBefore);
        });
      });
    }

    test('the apply button is >=44px at sm (NFR3)', async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await gotoDetail(page, true);
      await expectTouchTargets(page, ['.apply-action__apply']);
    });
  });
});
