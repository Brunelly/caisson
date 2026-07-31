# 0062 — Internal merged-apply gate (reason codes + candidate-fingerprint matching)

## Status

Accepted

## Context

Story #173 AC4 requires a hard, Caisson-internal safety boundary: no apply/promote action may proceed unless
the exact rack-change candidate's GitHub pull request is **merged** — enforced independently of GitHub branch
protection, at both the UI and the API. Forces:

- The gate must be the SINGLE source of truth for both the read DTO (which drives the UI apply banner) and the
  write path (drift-apply), so the two never disagree.
- It must match the **exact** candidate, not "the latest PR for the rack", so a newer unrelated merged PR
  cannot unlock an older/unrelated candidate.
- It must be fail-closed on missing/unknown/stale status.
- No dedicated `desired-state/promote` endpoint exists yet; the real enforcement point today is the drift-apply
  path (`DriftApplyController` → `DriftApplyJobService`).

## Decision

1. **A reusable `IPrMergeGate`** in `Caisson.Orchestration.Git` returning `PrMergeGateReason ∈ {Allowed,
   NoPrLinked, PrNotMerged}`. `EvaluateAsync(rackId, candidateFingerprint)` is the core (a future promote
   endpoint reuses it unchanged); `EvaluateForDriftItemAsync(item)` traces `DriftItem → DriftReport.DesiredRevisionId
   → DesiredStateVersion.CandidateFingerprint` (the canonical fingerprint, aligned with the PR link — see the
   alignment decision below) and calls the core.
2. **Exact-candidate matching, merged-only:** the gate matches the candidate fingerprint against
   `GitPullRequestLink.CandidateFingerprint` for the rack and allows apply only when a persisted
   `GitPullRequestStatusRecord.State == Merged` backs that exact link. No link → `NoPrLinked`; a link without a
   merged status → `PrNotMerged`. It depends on **merged** state only (branch protection governs whether GitHub
   permits the merge; Caisson's boundary is that the merge actually occurred). Fail-closed everywhere: an
   unresolved candidate, a missing link, or a missing/unmerged status all block.

   **Fingerprint alignment (resolves the earlier known limitation).** For the gate to match in the real
   pipeline, the two sides must be the same primitive over the same input. Ingestion now stamps every
   `DesiredStateVersion` with a **canonical `CandidateFingerprint`** — computed by projecting the just-materialised
   document into a `SupportedDesiredStateModel` via `BaselineIntentProjection` (the SAME projection the
   impact-preview / PR-baseline path uses) and hashing it through `CandidateFingerprint.Compute` (project →
   canonical render → length-framed SHA-256) — the identical primitive PR creation stamps on
   `GitPullRequestLink.CandidateFingerprint`. So a PR whose candidate resolves to the same rack-slug +
   per-port access-VLAN model reproduces the link's fingerprint, independently of the raw file's byte
   framing/whitespace, and a merged PR unlocks apply for its exact candidate (AC4). The raw, unframed
   `DesiredStateVersion.ContentHash` is unchanged and still keys the unchanged-file materialisation skip; the
   gate no longer reads it. A revision whose canonical fingerprint could not be derived (pre-alignment rows, or
   a document the projection cannot render) carries `null` and fails closed as `NoPrLinked`.

   Because the M1 ingestion schema carries no VLAN catalogue (ADR 0053), the projection synthesises one from the
   referenced access-VLAN ids (`vlan-{id}` names), so the fingerprint is over the rack slug + per-port
   access-VLAN intents. This is exact for the M1 `AccessVlanMismatch` remediation flow; a candidate authored
   with distinct VLAN *names/descriptions* would fingerprint differently and is the separate
   vlan-catalogue-persistence follow-up already tracked by ADR 0053, not a gap in this gate.
3. **Enforcement wired into the existing apply path** (no speculative promote controller): `DriftApplyController`
   invokes the gate AFTER rack/item/type validation but BEFORE `RequestApplyAsync`, returning RFC 7807 `409
   Conflict` with an exact PascalCase `reasonCode` (`PrNotMerged`/`NoPrLinked`) so a rejected call creates no job
   and reaches no driver. `DriftApplyJobService.RequestApplyAsync` re-checks the gate as defence-in-depth
   (throwing `PrMergeGateBlockedException` for any other caller).
4. **Shared reason codes** live in `Caisson.Domain.Git.GitPrGateReasonCodes` (PascalCase `Allowed`/`NoPrLinked`/
   `PrNotMerged`) so the API 409, the read DTO's `gateReasonCode`, and the frontend contract agree.
5. **Read DTO:** `RackPullRequestController` derives the read gate directly from the shown PR's persisted state
   (`Merged → CanApply=true/Allowed`, else `PrNotMerged`; no record → `NoPrLinked`) using the same reason
   vocabulary — a consistent no-link representation that leaks no repository metadata, gated by
   `CheckRackAccessAsync` first.

## Consequences

- Pre-existing *unit/API* drift-apply tests that do not set up PR links override `IPrMergeGate` with a
  permissive always-allow double so they keep exercising drift-apply mechanics; the gate has its own dedicated
  `PrMergeGateTests` / `PrMergeGateApiTests`. The full-loop `Caisson.VirtualRack.IntegrationTests` drift-apply
  e2e tests instead seed a real merged `GitPullRequestLink` for the ingested revision's canonical fingerprint
  (`MergedPrLinkTestSeeder`), so they clear the *real* gate and prove the ingest→PR-merge→apply→device-write
  loop end-to-end (AC4).
- **Fingerprint alignment (closed).** The gate originally compared `DesiredStateVersion.ContentHash` (SHA-256 of
  the raw ingested git file, unframed) to `GitPullRequestLink.CandidateFingerprint` (SHA-256 of the *canonical,
  length-prefixed* rendered YAML) — the same SHA-256 primitive over different inputs and framings, so they never
  matched for the same logical content and AC4's merged-unlock path was unreachable in production (fail-closed,
  never fail-open). This is now resolved by persisting a dedicated canonical `DesiredStateVersion.CandidateFingerprint`
  at ingestion, computed via `BaselineIntentProjection` + the identical `CandidateFingerprint.Compute` primitive
  PR creation uses (migration `AddDesiredStateVersionCandidateFingerprint`); the gate reads that column. A DB-free
  test (`CandidateFingerprintTests.Ingestion_projection_fingerprint_matches_the_pr_candidate_fingerprint`) pins
  the projection→render→hash alignment, and `PrMergeGateApiTests` derives both sides through the production
  `CandidateFingerprint` primitive instead of copying the raw ContentHash across.
- **Migration adds a nullable column.** Revisions ingested before this change carry `null` and fail closed
  (`NoPrLinked`) until re-ingested; no backfill is performed because the raw file bytes are not retained on the
  revision, so the fingerprint can only be re-derived by a fresh ingestion of the same commit.
- **Remaining follow-up (separate from this gate).** The PR-authoring/round-trip YAML schema
  (`apiVersion`/`metadata`/`spec`, emitted by `DesiredStateYamlRenderer` and committed by story #172) and the
  git-ingestion schema (`rackSlug`/`switches`, required by `DesiredStateValidator`) are not yet unified, so a
  Caisson-authored PR's committed file is not ingestible verbatim today. Demonstrating AC4 with a *real*
  (non-seeded) merged PR therefore also depends on reconciling those two schemas; that is tracked as ingestion
  follow-up, independent of this fingerprint alignment.
