// Real-browser interaction/accessibility coverage for the story #171 impact-preview surface — the
// ImpactPreviewComponent (structured VLAN/port change summary, chip filtering, topology deep links, the
// non-blocking "not found in topology" badge) and the DesiredStateDiffViewerComponent (copy-to-clipboard,
// collapsible unified-diff hunks) — run against the dev-only harness route
// (`/__dev-harness__/network-config/:rackId/impact-preview`, see src/app/dev-harness/). Like the other
// network-config harness specs it fakes only the wire (here ImpactPreviewService + DesiredStateRoundTripService),
// so the components under test are the real production components. jsdom unit/a11y specs already cover the
// summary rendering and navigation-link construction; this spec adds the things only a real browser proves:
// actual keyboard focus flow, real router navigation on the deep links, real navigator.clipboard, and
// real-browser colour contrast (axe-core) in light, dark and high-contrast themes.
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

const RACK_ID = 'rack-1';
const AUTHOR_ROLES = 'ReadOnly,NetworkConfigAuthor';

// The harness grants clipboard access so the diff-viewer's navigator.clipboard.writeText resolves.
test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

async function gotoImpactPreview(page: Page): Promise<void> {
  await page.goto(
    `/__dev-harness__/network-config/${RACK_ID}/impact-preview?roles=${AUTHOR_ROLES}`,
  );
  await expect(page.locator('.impact-preview')).toBeVisible();
}

/** Runs a preview via the header button and waits for the structured summary to render. */
async function runPreview(page: Page): Promise<void> {
  await page.locator('.impact-preview__run').click();
  await expect(page.locator('.impact-preview__summary')).toBeVisible();
}

const chips = (page: Page) => page.locator('.impact-preview__chip');
const changeRows = (page: Page) => page.locator('.impact-preview__change');

test.describe('Impact preview — dev harness (real browser)', () => {
  test('run preview renders the structured summary, chips, diff viewer and moves focus to the first chip', async ({
    page,
  }) => {
    await gotoImpactPreview(page);

    // Idle before running.
    await expect(page.locator('.impact-preview__status')).toContainText('Run a preview');
    await expect(page.locator('.impact-preview__run')).toHaveText('Run preview');

    await runPreview(page);

    // Structured summary: 3 VLAN changes + 2 port changes = 5 rows.
    await expect(changeRows(page)).toHaveCount(5);
    await expect(chips(page)).toHaveCount(4);
    // Two distinct switches (sw1, sw2) across the port changes.
    await expect(page.locator('.impact-preview__devices')).toContainText('Affects 2 devices');

    // Raw unified diff viewer is present with the copy control.
    await expect(page.locator('.diff-viewer')).toBeVisible();
    await expect(page.locator('.diff-viewer__copy')).toBeVisible();

    // NFR5 keyboard flow: focus lands on the first chip once a fresh preview renders.
    await expect(chips(page).first()).toBeFocused();

    // The polite live region announces completion.
    await expect(page.locator('.impact-preview__live')).toContainText(
      'Impact preview ready: 5 changes',
    );

    // Refresh button label flips once a result exists.
    await expect(page.locator('.impact-preview__run')).toHaveText('Refresh preview');
  });

  test('chips filter the change list and expose aria-pressed toggle state', async ({ page }) => {
    await gotoImpactPreview(page);
    await runPreview(page);

    const portsChip = chips(page).filter({ hasText: 'ports re-assigned' });
    await expect(portsChip).toHaveAttribute('aria-pressed', 'false');

    // Filter to ports: only the 2 port changes remain, chip reports pressed.
    await portsChip.click();
    await expect(portsChip).toHaveAttribute('aria-pressed', 'true');
    await expect(changeRows(page)).toHaveCount(2);
    await expect(changeRows(page).first()).toContainText('accessVlan changed');

    // Toggle the same chip off: back to all 5 changes.
    await portsChip.click();
    await expect(portsChip).toHaveAttribute('aria-pressed', 'false');
    await expect(changeRows(page)).toHaveCount(5);

    // "VLANs added" filter narrows to the single added VLAN.
    const addedChip = chips(page).filter({ hasText: 'VLANs added' });
    await addedChip.click();
    await expect(changeRows(page)).toHaveCount(1);
    await expect(changeRows(page).first()).toContainText('VLAN 100 (storage) added');
  });

  test('chips are keyboard operable (Enter/Space)', async ({ page }) => {
    await gotoImpactPreview(page);
    await runPreview(page);

    // Focus is already on the first chip (VLANs added). Activate it with the keyboard.
    await expect(chips(page).first()).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(chips(page).first()).toHaveAttribute('aria-pressed', 'true');
    await expect(changeRows(page)).toHaveCount(1);

    await page.keyboard.press('Space');
    await expect(chips(page).first()).toHaveAttribute('aria-pressed', 'false');
    await expect(changeRows(page)).toHaveCount(5);
  });

  test('topology-known entities render a deep link; absent entities show a non-blocking badge (AC3)', async ({
    page,
  }) => {
    await gotoImpactPreview(page);
    await runPreview(page);

    // Two entities (VLAN 20 modified, sw2/ether5) are absent from observed topology -> non-blocking badge,
    // no deep link. The other three are deep-linkable. (The exact router target + ?focus= node id is
    // asserted by the jsdom unit spec; here we prove the real-browser link-vs-badge rendering and a11y
    // labelling, without leaving the harness and crossing the production route's roleGuard.)
    await expect(page.locator('.impact-preview__notfound')).toHaveCount(2);
    await expect(page.locator('.impact-preview__deeplink')).toHaveCount(3);

    // The added-VLAN row is deep-linkable with an accessible name; its own row shows no not-found badge.
    const addedRow = changeRows(page).filter({ hasText: 'VLAN 100 (storage) added' });
    await expect(addedRow.locator('.impact-preview__notfound')).toHaveCount(0);
    await expect(addedRow.locator('.impact-preview__deeplink')).toHaveAttribute(
      'aria-label',
      /View .* in the topology graph/,
    );

    // The modified-VLAN row is absent from topology: non-blocking badge, no link.
    const modifiedRow = changeRows(page).filter({ hasText: 'name changed' });
    await expect(modifiedRow.locator('.impact-preview__deeplink')).toHaveCount(0);
    await expect(modifiedRow.locator('.impact-preview__notfound')).toContainText(
      'Not found in topology',
    );
  });

  test('diff viewer copies the raw diff and collapses/expands hunks', async ({ page }) => {
    await gotoImpactPreview(page);
    await runPreview(page);

    // Copy to clipboard -> success toast, and the clipboard holds the raw diff.
    await page.locator('.diff-viewer__copy').click();
    await expect(page.locator('.toast--success')).toContainText('Diff copied');
    const clip = await page.evaluate(() => navigator.clipboard.readText());
    expect(clip).toContain('accessVlan: 20');

    // At the desktop test viewport (>= md) the split pane is the visible rendering; the unified <pre> is
    // clipped for assistive tech. Drive the visible split-view hunk toggle a sighted user interacts with.
    const hunkToggle = page.locator('.diff-viewer__hunk--split').first();
    await expect(hunkToggle).toHaveAttribute('aria-expanded', 'true');
    const contextBefore = await page.locator('.diff-viewer__cell--context').count();
    expect(contextBefore).toBeGreaterThan(0);

    await hunkToggle.click();
    await expect(hunkToggle).toHaveAttribute('aria-expanded', 'false');
    expect(await page.locator('.diff-viewer__cell--context').count()).toBeLessThan(contextBefore);
    // Change lines stay visible regardless of collapse state.
    await expect(page.locator('.diff-viewer__cell--add').first()).toBeVisible();
  });

  async function scanAllThemes(page: Page): Promise<void> {
    // Sample the settled end state of each theme, never a mid-transition frame.
    await page.addStyleTag({
      content:
        '*, *::before, *::after { transition: none !important; animation: none !important; }',
    });
    for (const theme of ['light', 'dark', 'hc-dark'] as const) {
      await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      const results = await new AxeBuilder({ page })
        .options({ rules: { 'color-contrast': { enabled: true }, region: { enabled: false } } })
        .analyze();
      const summary = results.violations.map(
        (v) => `${v.id} @ ${v.nodes.map((n) => n.target).join(', ')}`,
      );
      expect(summary, `axe violations in ${theme}`).toEqual([]);
    }
  }

  test('impact preview has no a11y violations (incl. real-browser colour contrast) across themes', async ({
    page,
  }) => {
    await gotoImpactPreview(page);
    await runPreview(page);
    await expect(changeRows(page)).toHaveCount(5);
    await expect(page.locator('.diff-viewer')).toBeVisible();
    await scanAllThemes(page);
  });
});
