// Mechanical enforcement of AC1 ("no direct hex/rgb/hsl color literals in the app shell SCSS/CSS
// except within the design-token definition files"). Globs the explicit list of shell-owned SCSS files
// and regex-asserts none of them contain a hardcoded colour literal — every colour must come from a
// `var(--cds-*)` read instead. `_cds-tokens.scss` itself is intentionally NOT in this list: it's the
// one file allowed (in fact required) to declare the literal colour values.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const here = dirname(fileURLToPath(import.meta.url));

const SHELL_OWNED_SCSS_FILES = [
  'app-shell.component.scss',
  'sidebar-navigation/sidebar-navigation.component.scss',
  'rack-selector-topbar/rack-selector-topbar.component.scss',
  'theme-toggle/theme-toggle.component.scss',
  '../shared/connection-status/live-connection-status-bar.component.scss',
  '../shared/toast/toast-outlet.component.scss',
];

const COLOR_LITERAL = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/;

/** Strips `//` and `/* *\/` comments before scanning — SCSS comments legitimately reference things
 * like "Task #127", which would otherwise false-positive against the hex-colour pattern. */
function stripComments(source: string): string {
  return source.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
}

describe('Shell SCSS token usage (Story #119 AC1)', () => {
  it.each(SHELL_OWNED_SCSS_FILES)(
    '%s contains no hardcoded colour literal (reads var(--cds-*) only)',
    (relativePath) => {
      const content = stripComments(readFileSync(resolve(here, relativePath), 'utf8'));
      expect(content).not.toMatch(COLOR_LITERAL);
    },
  );

  // The mechanical check above is only meaningful if the regex genuinely catches disallowed patterns
  // and lets tokenised reads through — this pins that behaviour down directly.
  it('the guard regex catches disallowed literals and allows var(--cds-*) reads', () => {
    expect('color: #fff;').toMatch(COLOR_LITERAL);
    expect('color: #ffffff;').toMatch(COLOR_LITERAL);
    expect('background: rgba(0, 0, 0, 0.5);').toMatch(COLOR_LITERAL);
    expect('background: hsl(200 50% 50%);').toMatch(COLOR_LITERAL);
    expect('color: var(--cds-text-primary);').not.toMatch(COLOR_LITERAL);
    expect('background: var(--cds-glass-fill);').not.toMatch(COLOR_LITERAL);
  });
});
