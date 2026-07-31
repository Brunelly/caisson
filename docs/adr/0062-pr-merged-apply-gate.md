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
   → DesiredStateVersion.ContentHash` and calls the core.
2. **Exact-candidate matching, merged-only:** the gate matches the candidate fingerprint against
   `GitPullRequestLink.CandidateFingerprint` for the rack and allows apply only when a persisted
   `GitPullRequestStatusRecord.State == Merged` backs that exact link. No link → `NoPrLinked`; a link without a
   merged status → `PrNotMerged`. It depends on **merged** state only (branch protection governs whether GitHub
   permits the merge; Caisson's boundary is that the merge actually occurred). Fail-closed everywhere: an
   unresolved candidate, a missing link, or a missing/unmerged status all block.
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

- Pre-existing drift-apply tests, which do not set up PR links, override `IPrMergeGate` with a permissive
  always-allow double so they keep exercising drift-apply mechanics; the gate has its own dedicated
  `PrMergeGateTests` / `PrMergeGateApiTests`.
- **Known limitation / assumption (follow-up):** `DesiredStateVersion.ContentHash` (SHA-256 of the raw ingested
  git file) and `GitPullRequestLink.CandidateFingerprint` (SHA-256 of the *canonical, length-prefixed* rendered
  YAML via `DesiredStateContentHash`) are the same primitive but computed over different framings, so they are
  not byte-equal for the same logical content in the current M1 pipeline. The gate compares them directly (per
  this story's design and the [Unvalidated] story-172 linkage assumption). Keeping those two values aligned
  across the ingestion↔PR-creation boundary — e.g. by persisting the candidate fingerprint on the ingested
  revision, or by re-deriving it canonically — is a follow-up so the gate matches in production as well as in
  the fingerprint-aligned tests here. Until then the gate is correctly fail-closed (it blocks rather than
  wrongly allows).
