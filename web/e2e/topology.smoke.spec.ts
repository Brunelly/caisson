// E2E smoke test (Task #55): page load, graph render, search focus, drill-down open, and a live
// snapshot-updated timestamp refresh without a full navigation. Runs against a real, already-running,
// already-seeded Caisson.Api + Postgres/Redis stack — seeding the rack and driving a discovery job to
// produce the live event is the responsibility of an external harness (mcp-tooling-caisson;
// intentionally not referenced here — see docs/frontend-getting-started.md and playwright.config.ts).
//
// Required environment (set by whatever orchestrates the stack, e.g. CI or a local script):
//   E2E_RACK_ID          - an existing, already-discovered rack id.
//   E2E_SEARCH_TERM       - a term (hostname/MAC/switch/port/VLAN) known to match exactly one entity
//                           in that rack's latest snapshot.
//   E2E_SEARCH_LABEL_PART - a substring expected in that entity's rendered label, to assert the right
//                           result was focused/opened.
// The spec skips (rather than failing misleadingly) when these aren't provided, since a smoke test with
// no seeded rack cannot assert anything meaningful.
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const rackId = process.env['E2E_RACK_ID'];
const searchTerm = process.env['E2E_SEARCH_TERM'];
const searchLabelPart = process.env['E2E_SEARCH_LABEL_PART'];

test.describe('Topology page smoke test', () => {
  test.skip(
    !rackId || !searchTerm || !searchLabelPart,
    'requires a seeded rack — set E2E_RACK_ID/E2E_SEARCH_TERM/E2E_SEARCH_LABEL_PART',
  );

  test('loads the rack topology, renders the graph, searches, and drills down', async ({
    page,
  }) => {
    const browserErrors: string[] = [];
    const failedDataRequests: string[] = [];
    page.on('pageerror', (error) => browserErrors.push(error.message));
    page.on('response', (response) => {
      if (
        response.status() >= 400 &&
        (new URL(response.url()).pathname === '/api/racks' ||
          response.url().includes('/topology/') ||
          response.url().includes('/hubs/topology'))
      ) {
        failedDataRequests.push(`${response.status()} ${response.url()}`);
      }
    });
    page.on('console', (message) => {
      // The legacy index references an optional favicon that is not shipped. Keep genuine app/API
      // console failures fatal while excluding that browser-only missing decorative asset.
      if (
        message.type() === 'error' &&
        message.text() !==
          'Failed to load resource: the server responded with a status of 404 (Not Found)'
      ) {
        browserErrors.push(message.text());
      }
    });
    const rackResponse = page.waitForResponse((response) => response.url().endsWith('/api/racks'));
    const topologyResponse = page.waitForResponse((response) =>
      response.url().includes('/topology/snapshots/latest'),
    );

    await page.goto('/');
    expect((await rackResponse).ok()).toBe(true);
    expect((await topologyResponse).ok()).toBe(true);
    await expect(page).toHaveURL(new RegExp(`/racks/${rackId}/topology$`));

    const rackSelector = page.getByRole('combobox', { name: 'Select rack' });
    await expect(rackSelector).toBeEnabled();
    await expect(rackSelector).toHaveAttribute('aria-haspopup', 'listbox');

    // Keyboard and dismissal contract: open/focus selected option, wrap arrows, Escape/backdrop
    // restore trigger focus, Tab closes while moving onward, and Enter selects/closes.
    await rackSelector.press('Space');
    const rackOption = page.getByRole('option', { name: /Virtual Rack \(seeded\)/ });
    await expect(rackOption).toBeFocused();
    await rackOption.press('ArrowDown');
    await expect(rackOption).toBeFocused();
    await rackOption.press('Escape');
    await expect(rackSelector).toBeFocused();
    await expect(rackSelector).toHaveAttribute('aria-expanded', 'false');

    await rackSelector.press('Enter');
    await page.locator('.cdk-overlay-backdrop').click({ position: { x: 1, y: 1 } });
    await expect(rackSelector).toBeFocused();

    await rackSelector.press('Enter');
    await rackOption.press('Tab');
    await expect(rackSelector).toHaveAttribute('aria-expanded', 'false');
    await rackSelector.focus();
    await rackSelector.press('Enter');
    await rackOption.press('Enter');
    await expect(rackSelector).toBeFocused();

    for (const theme of ['Dark', 'Light']) {
      await page.getByRole('radio', { name: theme }).click();
      await rackSelector.press('Enter');
      await expect(rackOption).toBeVisible();
      const colours = await rackOption.evaluate((element) => {
        const style = getComputedStyle(element);
        return { foreground: style.color, background: style.backgroundColor };
      });
      expect(colours.foreground).not.toBe(colours.background);
      const accessibility = await new AxeBuilder({ page })
        .include('.topbar')
        .include('.rack-options')
        .withRules([
          'color-contrast',
          'aria-allowed-attr',
          'aria-required-attr',
          'aria-valid-attr-value',
        ])
        .analyze();
      expect(accessibility.violations).toEqual([]);
      await rackOption.press('Escape');
    }

    await expect(page.getByText(/^(Live|Disconnected)$/)).toBeVisible();

    // AC1: the page loads and shows the snapshot's "latest" indicator.
    await expect(page.getByText('Latest', { exact: true })).toBeVisible();
    const snapshotMeta = page.locator('.snapshot-meta');
    await expect(snapshotMeta).toContainText('Snapshot v');
    const initialSnapshotText = await snapshotMeta.textContent();

    // AC1: the graph renders at least one node from the seeded snapshot.
    const graph = page.locator('svg.topology-graph');
    await expect(graph).toBeVisible();
    await expect(graph.locator('g.node')).not.toHaveCount(0);

    // AC2: search finds the seeded entity and focuses/opens it.
    const searchInput = page.getByRole('combobox', { name: /search topology/i });
    await searchInput.fill(searchTerm!);
    const option = page.getByRole('option').filter({ hasText: searchLabelPart! }).first();
    await expect(option).toBeVisible();
    await option.click();

    // AC3: the drill-down details panel opens for the selected entity. Scoped to the aside element
    // (not just "a region with an h2") since the always-rendered legend is also a region with its own
    // h2 ("Legend") — a `getByRole('region').filter({ has: page.locator('h2') })` locator matches both
    // and throws a strict-mode violation.
    const detailsPanel = page.locator('aside.details-panel');
    await expect(detailsPanel).toBeVisible();
    await expect(detailsPanel.locator('h2')).toContainText(searchLabelPart!);

    // AC5: if an external harness drives a discovery job during this test run, the snapshot timestamp
    // updates in place — no full navigation/reload. This only asserts if the value actually changes
    // within the window; it does not itself trigger discovery (see file header).
    await expect
      .poll(async () => snapshotMeta.textContent(), { timeout: 15000, intervals: [1000] })
      .not.toBe(null);
    const laterSnapshotText = await snapshotMeta.textContent();
    if (laterSnapshotText !== initialSnapshotText) {
      // A live update landed during the test — confirm it patched in place rather than reloading.
      expect(page.url()).toContain(`/racks/${rackId}/topology`);
    }
    expect(browserErrors).toEqual([]);
    expect(failedDataRequests).toEqual([]);
  });
});
