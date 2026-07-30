# 0044 — Responsive visual regression: Chromium-only viewport projects, not device presets

## Status

Accepted

## Context

Story #123 needs `sm`/`md` viewport coverage for `topology-visual.spec.ts`/`drift-visual.spec.ts`
(Task #142) on top of the existing `lg`/`xl` coverage ADR 0040 established, plus a real touch-interaction
check that pan/zoom/tap on the topology graph and a tap-to-open on the details bottom-sheet actually work
under touch emulation (Task #143).

Two viewport-emulation approaches were available:

1. `devices['iPhone 13']` / `devices['iPad (gen 7)']`-style presets — these pull in `browserName: 'webkit'`
   plus `hasTouch: true`/`isMobile: true` by default.
2. Spreading `devices['Desktop Chrome']` with an explicit `viewport` override — same Chromium engine as
   the existing `chromium` project, just a narrower viewport.

Using device presets for the new `mobile`/`tablet` *visual* projects would mean maintaining TWO sets of
pixel baselines per new viewport (Chromium's anti-aliasing/font rendering differs from WebKit's, so a
single PNG can't serve both engines) — doubling Task #142's baseline-maintenance surface for a project
whose CI already pins on a single Chromium build (this file's own pre-existing header comment). Nothing
in the story requires WebKit-specific coverage; the DS re-skin is standard CSS (flex/grid/media-queries),
not engine-specific rendering.

## Decision

- **`mobile` (~390×844, `sm`) and `tablet` (~768×1024, `md`) Playwright projects both spread
  `devices['Desktop Chrome']`** with only `viewport` overridden — Chromium throughout, matching the
  existing `chromium` project's engine. Both projects set `testMatch: /.*-visual\.spec\.ts$/` so they run
  ONLY the two visual spec files; every other spec (harness/smoke/a11y) still runs exactly once, under
  `chromium`, exactly as before Task #142 — a device-preset project applied project-wide would have
  silently 3x'd every other spec's run time and introduced WebKit-only failures unrelated to this story.
- **`workers: 1`/`fullyParallel: false`/`snapshotPathTemplate`/`maxDiffPixelRatio: 0.01` are unchanged**
  — determinism and the existing baseline layout convention both carry over unmodified to the new
  projects.
- **Existing desktop baseline filenames are unchanged** (e.g. `graph-default-dark.png` still means the
  `chromium` project's shot). The visual specs derive a filename suffix from `testInfo.project.name`
  (empty for `chromium`, `-mobile`/`-tablet` otherwise) so new viewport coverage lands in new,
  distinctly-named files (`graph-default-dark-mobile.png`) rather than colliding with or silently
  replacing the desktop baseline `snapshotPathTemplate` would otherwise resolve to.
- **The real-touch requirement (pan/zoom/tap, Task #143) is answered by a dedicated `hasTouch: true` /
  `page.touchscreen` Playwright test**, not by making `mobile`/`tablet` touch-emulated projects. Touch
  emulation is a `use`-level flag orthogonal to viewport size; adding it project-wide would touch-emulate
  the *visual* screenshot tests too, which don't exercise interaction and gain nothing from it, while a
  single opt-in test targets exactly the interaction this task cares about (see the harness spec touch
  test this ADR's Task #143 companion work adds).

## Consequences

- Two browser engines were considered and Chromium-only was chosen everywhere — if a genuine WebKit/Safari
  rendering bug is ever suspected, it needs a separate, explicitly-scoped investigation; this story's
  suite cannot catch it.
- `mobile`/`tablet` baselines are additive, new files — no risk of accidentally overwriting/renaming an
  existing desktop baseline. A desktop (`chromium`) baseline changing is still always a real, reviewable
  diff.
- Running the full `web/e2e` suite now executes the two visual specs three times (once per project) but
  every other spec exactly once — visual-suite wall-clock roughly triples, other suites are unaffected.
