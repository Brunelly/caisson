// Real-browser interaction/accessibility coverage for the story #170 pre-flight validation surface —
// the ValidationIssuesPanel (grouped Errors/Warnings/Safety notices, live regions, focus-to-first-error)
// and the Create-PR acknowledgement dialog — run against the dev-only harness route
// (`/__dev-harness__/network-config/:rackId/...`, see src/app/dev-harness/). Like
// network-config-harness.spec.ts it fakes only the wire (here PreflightValidationService + PrService), so
// the shell, panel and dialog under test are the real production components.
//
// The pre-flight scenario is parameterised via `?preflight=` (clean | errors | warnings | mixed), read by
// the harness's fake PreflightValidationService at call time exactly like `?roles=` — see
// dev-harness.providers.ts. Every validate() call mints a fresh validationRunId, so the shell's
// stale-on-edit / warning-acknowledgement gating (AC3/AC4/AC5) behaves precisely as in production.
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

const RACK_ID = 'rack-1';
const AUTHOR_ROLES = 'ReadOnly,NetworkConfigAuthor';

type PreflightScenario = 'clean' | 'errors' | 'warnings' | 'mixed';

async function gotoShell(
  page: Page,
  scenario: PreflightScenario | null = null,
  roles: string | null = AUTHOR_ROLES,
): Promise<void> {
  const params = new URLSearchParams();
  if (roles) {
    params.set('roles', roles);
  }
  if (scenario) {
    params.set('preflight', scenario);
  }
  const query = params.toString();
  await page.goto(`/__dev-harness__/network-config/${RACK_ID}/vlans${query ? `?${query}` : ''}`);
  await expect(page.locator('.vlan-catalogue')).toBeVisible();
  // The pre-flight panel is always rendered (read-only users can view results — AC5).
  await expect(page.locator('.panel', { hasText: 'Validation issues' })).toBeVisible();
}

const validateButton = (page: Page) => page.locator('.network-config-shell__validate');
const createPrButton = (page: Page) => page.locator('.network-config-shell__create-pr');
const dialog = (page: Page) => page.locator('.create-pr-dialog');
const successToast = (page: Page) => page.locator('.toast--success');

/** Runs a validation via the action-bar Validate button and waits for the shimmer to resolve. */
async function runValidation(page: Page): Promise<void> {
  await validateButton(page).click();
  await expect(page.locator('.panel__loading')).toBeHidden();
}

test.describe('Network Config pre-flight — dev harness (real browser)', () => {
  test('clean run: no issues, Create PR enabled, dialog with no warnings creates the PR', async ({
    page,
  }) => {
    await gotoShell(page, 'clean');

    // Idle before validation: the panel prompts, and Create PR is disabled (no validation run yet).
    await expect(page.locator('.panel__empty')).toContainText('Run validation');
    await expect(createPrButton(page)).toBeDisabled();

    await runValidation(page);
    await expect(page.locator('.panel__clean')).toContainText('No issues found');
    await expect(createPrButton(page)).toBeEnabled();

    // No safety warnings -> the dialog offers a direct create.
    await createPrButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await expect(dialog(page)).toContainText('No safety warnings were raised');
    await expect(dialog(page).locator('.create-pr-dialog__submit')).toBeEnabled();
    await dialog(page).locator('.create-pr-dialog__submit').click();

    await expect(dialog(page)).toBeHidden();
    await expect(successToast(page)).toContainText('Pull request queued for creation.');
  });

  test('errors block PR: assertive live region, count chip, focus moves to first error, Create PR disabled', async ({
    page,
  }) => {
    await gotoShell(page, 'errors');
    await runValidation(page);

    const errorsGroup = page.locator('.group--errors');
    await expect(errorsGroup).toBeVisible();
    // NFR6: errors announced assertively; colour is never the sole indicator (a text "Error" label).
    await expect(errorsGroup).toHaveAttribute('role', 'alert');
    await expect(errorsGroup).toHaveAttribute('aria-live', 'assertive');
    await expect(errorsGroup.locator('.group__count')).toHaveText('1');
    await expect(errorsGroup.locator('.issue__severity').first()).toHaveText('Error');
    // AC1 example field-path format is surfaced verbatim (the RFC 6901 JSON Pointer).
    await expect(errorsGroup.locator('.issue__path').first()).toHaveText('/vlanCatalogue/1/id');

    // AC6: keyboard focus moves to the first error the moment validation completes.
    await expect(page.locator('.issue[data-group="errors"]').first()).toBeFocused();

    // PR creation is blocked while any error stands.
    await expect(createPrButton(page)).toBeDisabled();
  });

  test('safety warning: non-blocking, but the Create-PR dialog gates submit on acknowledgement', async ({
    page,
  }) => {
    await gotoShell(page, 'warnings');
    await runValidation(page);

    const safetyGroup = page.locator('.group--safety');
    await expect(safetyGroup).toBeVisible();
    await expect(safetyGroup).toHaveAttribute('aria-live', 'polite');
    await expect(safetyGroup.locator('.issue__severity').first()).toHaveText('Safety');
    await expect(safetyGroup.locator('.issue__message').first()).toContainText('uplink');

    // Warnings are non-blocking: Create PR is enabled, but the dialog must be acknowledged.
    await expect(createPrButton(page)).toBeEnabled();
    await createPrButton(page).click();
    await expect(dialog(page)).toBeVisible();

    const submit = dialog(page).locator('.create-pr-dialog__submit');
    await expect(submit).toBeDisabled();

    // Acknowledge the single safety warning -> submit unlocks -> creating the PR succeeds.
    await dialog(page).locator('input[type="checkbox"]').check();
    await expect(submit).toBeEnabled();
    await submit.click();
    await expect(dialog(page)).toBeHidden();
    await expect(successToast(page)).toContainText('Pull request queued for creation.');
  });

  test('dialog dismissals make ZERO API call: Escape closes and restores focus; backdrop click closes', async ({
    page,
  }) => {
    await gotoShell(page, 'warnings');
    await runValidation(page);

    // --- Escape closes and returns focus to the trigger, with no PR created ---
    await createPrButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(dialog(page)).toBeHidden();
    await expect(createPrButton(page)).toBeFocused();
    await expect(successToast(page)).toHaveCount(0);

    // --- Backdrop click closes, again with no PR created ---
    await createPrButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await page.locator('.cds-overlay-backdrop').click({ position: { x: 5, y: 5 } });
    await expect(dialog(page)).toBeHidden();
    await expect(successToast(page)).toHaveCount(0);
  });

  test('stale-on-edit (TOCTOU): editing the draft after a clean validation re-blocks PR creation', async ({
    page,
  }) => {
    await gotoShell(page, 'clean');
    await runValidation(page);
    await expect(createPrButton(page)).toBeEnabled();

    // Any draft edit clears the validation run — the panel returns to its "run validation" prompt and
    // Create PR is disabled again until a fresh validation is run (AC3/AC4).
    await page.locator('.vlan-catalogue__add').click();
    const addDialog = page.locator('.vlan-dialog');
    await expect(addDialog).toBeVisible();
    await addDialog.locator('#vlan-dialog-id').fill('30');
    await addDialog.locator('#vlan-dialog-name').fill('guest');
    await addDialog.locator('.vlan-dialog__submit').click();
    await expect(addDialog).toBeHidden();

    await expect(page.locator('.panel__empty')).toContainText('Run validation');
    await expect(createPrButton(page)).toBeDisabled();
  });

  test('read-only user can view the panel but has no Validate/Create-PR controls (AC5)', async ({
    page,
  }) => {
    await gotoShell(page, 'clean', 'ReadOnly');
    await expect(page.locator('.panel', { hasText: 'Validation issues' })).toBeVisible();
    await expect(validateButton(page)).toHaveCount(0);
    await expect(createPrButton(page)).toHaveCount(0);
    // The panel's own Re-validate action is hidden for principals who cannot run validation.
    await expect(page.getByRole('button', { name: 'Re-validate configuration' })).toHaveCount(0);
  });

  async function scanAllThemes(page: Page): Promise<void> {
    // Sample the settled end state of each theme, never a mid-transition frame (see
    // network-config-harness.spec.ts's scanAllThemes for the rationale).
    await page.addStyleTag({
      content:
        '*, *::before, *::after { transition: none !important; animation: none !important; }',
    });
    for (const theme of ['light', 'dark', 'hc-dark'] as const) {
      await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      const results = await new AxeBuilder({ page })
        .options({ rules: { 'color-contrast': { enabled: true }, region: { enabled: false } } })
        .analyze();
      expect(results.violations, `axe violations in ${theme}`).toEqual([]);
    }
  }

  test('mixed issue panel has no a11y violations (incl. real-browser colour contrast) across themes', async ({
    page,
  }) => {
    await gotoShell(page, 'mixed');
    await runValidation(page);
    await expect(page.locator('.group--errors')).toBeVisible();
    await expect(page.locator('.group--warnings')).toBeVisible();
    await expect(page.locator('.group--safety')).toBeVisible();
    await scanAllThemes(page);
  });

  test('Create-PR acknowledgement dialog has no a11y violations across themes', async ({
    page,
  }) => {
    await gotoShell(page, 'warnings');
    await runValidation(page);
    await createPrButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await scanAllThemes(page);
  });
});
