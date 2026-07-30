// Mechanical enforcement that the topology/drift/shared-badge components migrated onto --cds-* tokens
// (ADR 0038, Task #132) never regain a hardcoded colour literal. Mirrors shell/token-usage.spec.ts's
// exact regex + comment-stripping helper (same reasoning: SCSS comments legitimately reference things
// like "Task #129", which would otherwise false-positive against the hex-colour pattern), extended to
// the closed set of files that story #120 retargeted from --color-* to --cds-*, and (Story #121, Task
// #135) to the files the Caisson-DS re-skin touched or added. `_cds-tokens.scss` itself is intentionally
// NOT in this list — it's the one file allowed (in fact required) to declare the literal colour values.
// `_cds-topology-tokens.scss` (ADR 0039) IS in this list even though it's also a token-declaration file:
// unlike `_cds-tokens.scss`, every one of its values is itself a `var(--cds-*)`/`color-mix(...)` alias
// with zero literals of its own, so the guard is meaningful there too.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const here = dirname(fileURLToPath(import.meta.url));

// The ~13 files story #120 retargeted onto --cds-* — real .scss stylesheets and the inline `styles:
// [...]` arrays of components too small to warrant a separate stylesheet.
const TOPOLOGY_DRIFT_TOKEN_FILES = [
  'topology/graph/topology-graph.component.scss',
  'topology/details/topology-details-panel.component.scss',
  'topology/search/topology-search.component.scss',
  'topology/legend/topology-legend.component.ts',
  'topology/topology-page.component.ts',
  'drift/list/drift-reports-list.component.scss',
  'drift/detail/drift-report-details.component.scss',
  'drift/apply/apply-action.component.scss',
  'drift/apply/apply-confirmation-dialog.component.scss',
  'drift/apply/job-status-timeline.component.scss',
  'drift/apply/job-status-badge.component.ts',
  'drift/audit/audit-record-view.component.scss',
  'shared/badge/status-badge.component.ts',
  // Story #121 (Task #135) additions — the topology token-alias layer plus every file the Caisson-DS
  // re-skin touched or added.
  'shared/styles/_cds-topology-tokens.scss',
  'shared/connection-status/live-connection-status-bar.component.scss',
  'topology/discovery-status/discovery-job-status-widget.component.scss',
  // Story #168 — Network Config authoring (VLAN catalogue + port intent) style files.
  'network-config/network-config-shell.component.scss',
  'network-config/shared/discard-changes-dialog.component.scss',
  'network-config/vlan-catalogue/vlan-catalogue.component.scss',
  'network-config/vlan-catalogue/vlan-form-dialog.component.scss',
  'network-config/port-intent/port-intent.component.scss',
  'network-config/port-intent/port-intent-editor.component.scss',
];

const COLOR_LITERAL = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/;
const CDS_VAR_READ = /var\(--cds-[a-z0-9-]+\)/;
const OLD_COLOR_VAR_READ = /var\(--color-[a-z0-9-]+\)/;

/** Strips `//` and `/* *\/` comments before scanning — SCSS/TS comments legitimately reference things
 * like "Task #129", which would otherwise false-positive against the hex-colour pattern. */
function stripComments(source: string): string {
  return source.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
}

describe('Topology/drift/badge token usage (Story #120, ADR 0038)', () => {
  it.each(TOPOLOGY_DRIFT_TOKEN_FILES)(
    '%s contains no hardcoded colour literal and no --color-* read (reads var(--cds-*) only)',
    (relativePath) => {
      const content = stripComments(readFileSync(resolve(here, relativePath), 'utf8'));
      expect(content).not.toMatch(COLOR_LITERAL);
      expect(content).not.toMatch(OLD_COLOR_VAR_READ);
    },
  );

  // job-status-badge.component.ts is excluded here: it's a thin wrapper that delegates entirely to
  // <app-status-badge> (Task #130) and declares no `styles` of its own, so it legitimately has zero
  // --cds-* reads — the "no literal / no old layer" guard above still applies to it.
  const FILES_WITH_OWN_STYLES = TOPOLOGY_DRIFT_TOKEN_FILES.filter(
    (f) => f !== 'drift/apply/job-status-badge.component.ts',
  );

  it.each(FILES_WITH_OWN_STYLES)('%s reads at least one --cds-* token', (relativePath) => {
    const content = stripComments(readFileSync(resolve(here, relativePath), 'utf8'));
    expect(content).toMatch(CDS_VAR_READ);
  });

  // The mechanical check above is only meaningful if the regex genuinely catches disallowed patterns
  // and lets tokenised reads through — this pins that behaviour down directly.
  it('the guard regex catches disallowed literals/old-layer reads and allows var(--cds-*) reads', () => {
    expect('color: #fff;').toMatch(COLOR_LITERAL);
    expect('color: #ffffff;').toMatch(COLOR_LITERAL);
    expect('background: rgba(0, 0, 0, 0.5);').toMatch(COLOR_LITERAL);
    expect('background: hsl(200 50% 50%);').toMatch(COLOR_LITERAL);
    expect('color: var(--color-text);').toMatch(OLD_COLOR_VAR_READ);
    expect('color: var(--cds-text-primary);').not.toMatch(COLOR_LITERAL);
    expect('color: var(--cds-text-primary);').not.toMatch(OLD_COLOR_VAR_READ);
    expect('background: var(--cds-surface-elevated);').toMatch(CDS_VAR_READ);
  });
});
