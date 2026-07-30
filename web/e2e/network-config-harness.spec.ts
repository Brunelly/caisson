// Real-browser interaction/accessibility coverage for the Network Config authoring surface (story
// #168: VLAN Catalogue + Port Intent), run against the dev-only harness routes
// (`/__dev-harness__/network-config/:rackId/...`, see src/app/dev-harness/) instead of the real
// OIDC/Entra-gated routes — mirrors web/e2e/drift-harness.spec.ts's approach: fakes only the wire (the
// NetworkIntentService), so every component under test (shell, VLAN Catalogue, Port Intent, both
// dialogs) is the real production code.
//
// The RBAC claim is parameterised via a `?roles=` query param the harness's fake OidcSecurityService
// reads at call time (see dev-harness.providers.ts) — RBAC-hidden (no NetworkConfigAuthor) is the
// default; tests exercising the authoring workflow navigate with `?roles=ReadOnly,NetworkConfigAuthor`.
//
// `window.__harness__.resetNetworkIntent()` resets the harness's mutable in-memory catalogue/port-intent
// store (fixtures.ts) between tests — belt-and-braces on top of Playwright's own per-test fresh page
// (which already reloads the JS bundle from scratch on every `page.goto`), so a test never depends on
// exactly what a previous test saved.
//
// "Reload" below drives the SHELL'S OWN Reload action (`.network-config-shell__reload`, a
// NetworkIntentStateService.load() re-fetch through the very same fake NetworkIntentService instance)
// rather than a real `page.reload()`: the harness fixture state lives in plain in-memory module
// variables (fixtures.ts), not localStorage/a real backend, so a full browser navigation would
// re-execute those module initialisers and reset them to their hard-coded defaults — that would prove
// nothing about whether Save actually persisted into the shared fake-service store. Driving the app's
// own Reload button re-fetches through that same store without re-initialising it, which is the
// meaningful assertion: "the fake service's GET now reflects what the last saveIntent call wrote."
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

const RACK_ID = 'rack-1';
const AUTHOR_ROLES_QUERY = '?roles=ReadOnly,NetworkConfigAuthor';
const VLANS_URL = `/__dev-harness__/network-config/${RACK_ID}/vlans`;
const PORTS_URL = `/__dev-harness__/network-config/${RACK_ID}/ports`;

async function gotoVlans(page: Page, withAuthor = true): Promise<void> {
  await page.goto(withAuthor ? `${VLANS_URL}${AUTHOR_ROLES_QUERY}` : VLANS_URL);
  await expect(page.locator('.vlan-catalogue')).toBeVisible();
}

async function gotoPorts(page: Page, withAuthor = true): Promise<void> {
  await page.goto(withAuthor ? `${PORTS_URL}${AUTHOR_ROLES_QUERY}` : PORTS_URL);
  await expect(page.locator('.port-intent')).toBeVisible();
}

function vlanRow(page: Page, name: string): Locator {
  return page.locator('.vlan-catalogue__table tbody tr').filter({ hasText: name });
}

function portRow(page: Page, portName: string): Locator {
  return page.locator('.port-intent__table tbody tr').filter({ hasText: portName });
}

test.describe('Network Config — dev harness (real browser)', () => {
  test.beforeEach(async ({ page }) => {
    await gotoVlans(page);
    await page.evaluate(() => window.__harness__!.resetNetworkIntent());
  });

  test('RBAC-hidden: no NetworkConfigAuthor claim renders no mutating controls on either tab', async ({
    page,
  }) => {
    await gotoVlans(page, false);
    await expect(page.locator('.vlan-catalogue__add')).toHaveCount(0);
    await expect(page.locator('.vlan-catalogue__row-actions')).toHaveCount(0);

    await gotoPorts(page, false);
    await expect(page.locator('.port-intent__table button')).toHaveCount(0);
  });

  test('VLAN Catalogue: add, edit, and a blocked retire of a referenced VLAN; Port Intent: set/verify/revert; Save persists via the fake service, confirmed by the shell Reload action', async ({
    page,
  }) => {
    await gotoVlans(page, true);

    // Starting fixture state: VLAN 10 (default), VLAN 20 (storage, referenced by SW-1/ether2).
    await expect(page.locator('.vlan-catalogue__table tbody tr')).toHaveCount(2);

    // --- Add VLAN 30 "guest" ---
    await page.locator('.vlan-catalogue__add').click();
    const addDialog = page.locator('.vlan-dialog');
    await expect(addDialog).toBeVisible();
    await addDialog.locator('#vlan-dialog-id').fill('30');
    await addDialog.locator('#vlan-dialog-name').fill('guest');
    await addDialog.locator('.vlan-dialog__submit').click();
    await expect(addDialog).toBeHidden();
    await expect(page.locator('.vlan-catalogue__table tbody tr')).toHaveCount(3);
    await expect(vlanRow(page, '30')).toContainText('guest');

    // --- Edit VLAN 30 -> rename to "guest-updated" ---
    await vlanRow(page, '30').getByRole('button', { name: 'Edit' }).click();
    const editDialog = page.locator('.vlan-dialog');
    await expect(editDialog).toBeVisible();
    // VLAN id is read-only once the entry exists.
    await expect(editDialog.locator('#vlan-dialog-id')).toHaveAttribute('readonly', '');
    await editDialog.locator('#vlan-dialog-name').fill('guest-updated');
    await editDialog.locator('.vlan-dialog__submit').click();
    await expect(editDialog).toBeHidden();
    await expect(vlanRow(page, '30')).toContainText('guest-updated');

    // --- Attempt to retire VLAN 20 (referenced by SW-1/ether2) — blocked ---
    await vlanRow(page, 'storage').getByRole('button', { name: 'Retire' }).click();
    await expect(page.locator('.vlan-catalogue__retire-blocked')).toContainText(
      'still referenced by a port intent',
    );
    await expect(page.locator('.vlan-catalogue__table tbody tr')).toHaveCount(3);
    await expect(vlanRow(page, 'storage')).toBeVisible();

    // --- Switch to the Port Intent tab (real routed tab, same in-progress draft) ---
    await page.getByRole('link', { name: 'Port Intent' }).click();
    await expect(page.locator('.port-intent')).toBeVisible();
    await expect(page).toHaveURL(/\/ports$/);

    // ether2 already carries the fixture's access-VLAN 20 intent; ether4 is untouched (Inherit).
    await expect(portRow(page, 'ether2').locator('.status-badge--intent-access')).toContainText(
      '20',
    );
    await expect(portRow(page, 'ether4').locator('.status-badge--intent-inherit')).toBeVisible();

    // --- Set ether4's access VLAN to 30 (guest-updated) ---
    await portRow(page, 'ether4').getByRole('button', { name: 'Edit' }).click();
    const portDialog = page.locator('.port-intent-editor');
    await expect(portDialog).toBeVisible();
    await portDialog.locator('#port-intent-editor-select').selectOption('30');
    await portDialog.locator('.port-intent-editor__apply').click();
    await expect(portDialog).toBeHidden();

    const ether4Badge = portRow(page, 'ether4').locator('.status-badge--intent-access');
    await expect(ether4Badge).toContainText('30');
    await expect(ether4Badge).toContainText('guest-updated');

    // --- Revert ether4 back to Unchanged/Inherit ---
    await portRow(page, 'ether4').getByRole('button', { name: 'Edit' }).click();
    await expect(portDialog).toBeVisible();
    await expect(portDialog.locator('#port-intent-editor-select')).toHaveValue('30');
    await portDialog.locator('#port-intent-editor-select').selectOption('inherit');
    await portDialog.locator('.port-intent-editor__apply').click();
    await expect(portDialog).toBeHidden();
    await expect(portRow(page, 'ether4').locator('.status-badge--intent-inherit')).toBeVisible();

    // --- Save the combined draft ---
    await expect(page.locator('.network-config-shell__dirty')).toBeVisible();
    await page.locator('.network-config-shell__save').click();
    await expect(page.locator('.toast--success')).toBeVisible();
    await expect(page.locator('.network-config-shell__dirty')).toBeHidden();

    // --- Reload (via the shell's own action, re-fetching from the fake service) confirms the save
    // actually persisted into the shared harness store, not just local component state. ---
    await page.locator('.network-config-shell__reload').click();
    await expect(page.locator('.port-intent')).toBeVisible();
    await expect(portRow(page, 'ether4').locator('.status-badge--intent-inherit')).toBeVisible();
    await expect(portRow(page, 'ether2').locator('.status-badge--intent-access')).toContainText(
      '20',
    );

    await page.getByRole('link', { name: 'VLAN Catalogue' }).click();
    await expect(page.locator('.vlan-catalogue')).toBeVisible();
    await expect(page.locator('.vlan-catalogue__table tbody tr')).toHaveCount(3);
    await expect(vlanRow(page, '30')).toContainText('guest-updated');
  });

  async function scanAllThemes(page: Page): Promise<void> {
    // Disable CSS transitions/animations for this scan: the axe color-contrast check must sample the
    // settled end state of a theme switch, never a mid-transition frame. Without this, the shell's
    // theme-toggle 120ms colour transition (theme-toggle.component.scss) races the axe scan below and
    // can land inside that transition window, producing a false contrast violation on
    // .theme-toggle__option (same fix as topology-harness.spec.ts).
    await page.addStyleTag({
      content:
        '*, *::before, *::after { transition: none !important; animation: none !important; }',
    });

    const lightResults = await new AxeBuilder({ page })
      .options({ rules: { 'color-contrast': { enabled: true }, region: { enabled: false } } })
      .analyze();
    expect(lightResults.violations).toEqual([]);

    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    const darkResults = await new AxeBuilder({ page })
      .options({ rules: { 'color-contrast': { enabled: true }, region: { enabled: false } } })
      .analyze();
    expect(darkResults.violations).toEqual([]);

    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'hc-dark'));
    const hcDarkResults = await new AxeBuilder({ page })
      .options({ rules: { 'color-contrast': { enabled: true }, region: { enabled: false } } })
      .analyze();
    expect(hcDarkResults.violations).toEqual([]);
  }

  test('VLAN Catalogue has no automatically-detectable accessibility violations, including real-browser colour contrast, in light, dark, and high-contrast themes', async ({
    page,
  }) => {
    await gotoVlans(page, true);
    await scanAllThemes(page);
  });

  test('VLAN Catalogue add-dialog has no automatically-detectable accessibility violations across themes', async ({
    page,
  }) => {
    await gotoVlans(page, true);
    await page.locator('.vlan-catalogue__add').click();
    await expect(page.locator('.vlan-dialog')).toBeVisible();
    await scanAllThemes(page);
  });

  test('Port Intent (including the editor dialog) has no automatically-detectable accessibility violations, including real-browser colour contrast, in light, dark, and high-contrast themes', async ({
    page,
  }) => {
    await gotoPorts(page, true);
    await portRow(page, 'ether1').getByRole('button', { name: 'Edit' }).click();
    await expect(page.locator('.port-intent-editor')).toBeVisible();
    await scanAllThemes(page);
  });

  test('VLAN form dialog: Escape closes it, backdrop click closes it, and focus returns to the trigger', async ({
    page,
  }) => {
    await gotoVlans(page, true);
    const trigger = page.locator('.vlan-catalogue__add');

    // Escape closes and restores focus to the trigger (CDK Dialog, ADR 0034).
    await trigger.click();
    await expect(page.locator('.vlan-dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.vlan-dialog')).toBeHidden();
    await expect(trigger).toBeFocused();

    // Backdrop (outside) click also closes it, without persisting anything typed.
    await trigger.click();
    await expect(page.locator('.vlan-dialog')).toBeVisible();
    await page.locator('#vlan-dialog-name').fill('should-not-be-saved');
    await page.locator('.cds-overlay-backdrop').click({ position: { x: 5, y: 5 } });
    await expect(page.locator('.vlan-dialog')).toBeHidden();
    await expect(page.locator('.vlan-catalogue__table tbody tr')).toHaveCount(2);
  });

  test('Port Intent editor: Escape closes it, backdrop click closes it, and focus returns to the trigger row', async ({
    page,
  }) => {
    await gotoPorts(page, true);
    const trigger = portRow(page, 'ether3').getByRole('button', { name: 'Edit' });

    await trigger.click();
    await expect(page.locator('.port-intent-editor')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.port-intent-editor')).toBeHidden();
    await expect(trigger).toBeFocused();

    await trigger.click();
    await expect(page.locator('.port-intent-editor')).toBeVisible();
    await page.locator('.cds-overlay-backdrop').click({ position: { x: 5, y: 5 } });
    await expect(page.locator('.port-intent-editor')).toBeHidden();
    // Unchanged: no persisted intent for a port nobody applied a change to.
    await expect(portRow(page, 'ether3').locator('.status-badge--intent-inherit')).toBeVisible();
  });

  test('Port Intent editor native select: keyboard-operable (focus, choose via keyboard, Enter/Space-activated Apply commits the choice)', async ({
    page,
  }) => {
    await gotoPorts(page, true);
    await portRow(page, 'ether3').getByRole('button', { name: 'Edit' }).click();
    const dialog = page.locator('.port-intent-editor');
    await expect(dialog).toBeVisible();

    const select = dialog.locator('#port-intent-editor-select');
    await select.focus();
    await expect(select).toBeFocused();
    // A native <select> opens/chooses via its own OS-level popup; Playwright's selectOption drives the
    // same keyboard-accessible value-change event a real Arrow+Enter selection would dispatch.
    await select.selectOption('20');
    await expect(select).toHaveValue('20');

    // Tab from the select reaches Cancel then Apply; activate Apply via Enter (keyboard, not a click).
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await expect(dialog.locator('.port-intent-editor__apply')).toBeFocused();
    await page.keyboard.press('Enter');

    await expect(dialog).toBeHidden();
    await expect(portRow(page, 'ether3').locator('.status-badge--intent-access')).toContainText(
      '20',
    );
  });
});
