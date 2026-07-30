# 0042 — Drift/apply visual regression baselines (Playwright screenshots)

## Status

Accepted

## Context

Story #122 (Task #139) needs the same visual-regression protection ADR 0040 gave the topology re-skin,
now for the drift/apply surfaces this story touched: the drift reports list, drift item detail view,
apply confirmation dialog, the job-status timeline's new connected stepper, and the audit record view.
The mechanism decision itself is not re-litigated — ADR 0040 already settled "Playwright screenshots
against the dev-harness route, bounded per-locator crops, masked timestamps, `maxDiffPixelRatio: 0.01`,
`snapshotPathTemplate` under `e2e/__screenshots__/`" for this exact class of problem, and this story's
target surfaces are additively covered by the very same fixture-backed, fully-offline dev-harness routes
(`/__dev-harness__/drift/:rackId[...]`) that `web/e2e/drift-harness.spec.ts` (story #67) already
exercises — no live OIDC tenant, backend, or seeded drift data needed here either.

One drift/apply-specific wrinkle: unlike topology's `.snapshot-meta`/`.djs-widget__meta` hooks, the
drift/audit views don't have a class on every rendered date — `audit-view__fields` (Requested/Claimed)
renders two dates with no distinguishing hook per field, and `drift-list__table`'s "Detected" column
cell has no dedicated class either. Adding new classes purely to give Playwright something to mask would
be an out-of-scope template change for a visual-only story with a "no new class hooks beyond what's
already agreed" bias. The fixture dates (`harnessSnapshotMeta().createdAt`, fixed to `2026-01-01T12:00:00`
local) are wall-clock-independent and rendered by the same pinned Linux/UTC-consistent CI environment
every run, so — unlike topology's rationale, which masked defensively — masking is applied only where an
existing class hook already exists (`.drift-detail__detected`, `.audit-view__trail-date`); the remaining
date cells are left genuinely un-masked since the fixture value truly cannot vary between the baseline-
generation run and a later comparison run in the same pinned environment.

## Decision

Add `web/e2e/drift-visual.spec.ts`, structurally identical to `topology-visual.spec.ts`: for each of the
three themes (dark/light/hc-dark, seeded via `page.addInitScript` before navigation), capture
`toHaveScreenshot()` baselines for:

- the drift reports list panel (`.drift-list`, harness default fixture — one High-severity
  `AccessVlanMismatch` row)
- the drift item detail view (`.drift-detail`, no `DriftApply` permission — the read-only path)
- the apply confirmation dialog (`.apply-dialog`, opened via `.apply-action__apply` with
  `?roles=ReadOnly,DriftApply`)
- the job-status timeline's connected stepper mid-flight (`app-job-status-timeline`, captured after
  submitting the dialog and driving a simulated `Executing` hub event through
  `window.__harness__.hub.simulateDriftApplyJobStatusChanged` — the exact same interaction
  `drift-harness.spec.ts`'s live-status test already performs, reused here purely to reach a visually
  interesting `done`/`active`/`pending` mix rather than the flat initial `Pending` state)
- the audit record view (`.audit-view`, direct navigation to the harness's stable job URL)

15 screenshots total (5 states × 3 themes). Every capture is a bounded per-locator crop, never
full-page, matching ADR 0040's rationale (small, meaningful, low-noise PNGs). `drift-visual.spec.ts`
needs no `playwright.config.ts` changes — it lives under the same `testDir: './e2e'` and inherits the
existing `maxDiffPixelRatio`/`snapshotPathTemplate`/chromium-only project, so it runs in the same CI
Playwright job as `topology-visual.spec.ts` automatically.

Baselines here were generated in this task's sandboxed environment's own downloaded Chromium build (no
access to swap in the pinned CI/mcp-tooling browser from this environment). ADR 0040's own caveat
already applies without modification: **baselines must be regenerated from the pinned CI/mcp-tooling
Linux Chromium build** before being treated as a stable gate — confirmed empirically in this task, where
re-running the *existing, already-committed* `topology-visual.spec.ts` baselines against this sandbox's
Chromium build failed on ~5-6% pixel diffs purely from font-rendering/sub-pixel differences, with zero
source changes to topology. The new `drift-visual.spec.ts` baselines committed here are internally
consistent (generated and verified against each other in one run) but should be treated as a first draft
pending a regeneration pass on the pinned CI browser, exactly as any future intentional visual change to
either spec file already requires per ADR 0040.

## Consequences

- The re-skin's five drift/apply surfaces now have the same pixel-level regression gate topology already
  has, closing the coverage gap for this story.
- A future, unrelated change to any of the five captured surfaces requires a deliberate
  `--update-snapshots` regeneration (against the pinned browser) + review of the new baseline PNG in the
  same PR, the same ongoing cost ADR 0040 already accepted.
- Two of five states (`drift-detail`, `audit-view`) mask only the fields an existing class hook reaches;
  the audit view's Requested/Claimed dates and the list's Detected column remain unmasked, relying on the
  fixture's fixed date and pinned-environment consistency rather than a mask. If this ever proves flaky,
  the fix is to add per-field date-value class hooks (`audit-view__field--date` or similar) — a small,
  additive, still visual-only follow-up, not a re-architecture.
- The job-status-timeline baseline only exercises the stage-ladder branch (no `steps[]`) because the dev
  harness's `getJob()` fixture always returns an empty `steps` array — the detailed per-step branch has
  no reachable non-empty state in the harness today. Extending the harness fixture to support a populated
  `steps[]` variant (mirroring the `?discoveryStatus=` pattern) is a reasonable follow-up but out of
  scope for this visual-only story.
