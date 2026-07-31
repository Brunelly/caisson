# 0055 — Impact-preview API endpoints and RBAC

## Status

Accepted

## Context

Story #171 exposes the impact-preview compute/cache pipeline behind two rack-scoped endpoints (Tasks
#196/#202): a POST that computes (or serves from cache) the diff between a rack's latest ingested
desired-state revision and a candidate YAML, and a GET that retrieves a previously-computed preview by its
candidate id. Forces:

- Read Only users must be able to preview (persona 3 / AC3), but a preview POST is side-effecting (it may
  insert a cache row).
- Cross-rack access must not leak baseline metadata or diff content (AC4 / NFR2).
- Invalid YAML must 400 with line/column and write no cache row; a missing baseline must 409 with actionable
  guidance (AC5).
- The response must be observable (cache-hit ratio, diff-compute time, structured logs) without logging any
  secret/payload (NFR4).

## Decision

1. **Both endpoints are gated by `AuthorizationPolicies.TopologyRead`**, not the author permission, so a
   Read Only user can preview (AC3). Because the POST is a non-GET action gated by a *read* policy, the new
   `DesiredStateImpactPreviewController` is added to `ReadOnlyGuardTests.NonGetControllerAllowList` AND
   exempted from the write-policy assertion (a new "read-shaped preview" exemption, mirroring the existing
   HMAC-webhook exemption) — the preview is a read-shaped-but-side-effecting cache write, not a device/state
   mutation.
2. **Cross-rack access returns 404, not AC4's literal 403.** The controller follows the codebase's
   established leak-safe convention (`CheckRackAccessAsync` → 404, no existence oracle, ADR 0013 /
   `DiscoveryControllerBase`): an inaccessible rack is indistinguishable from a non-existent one, so no
   baseline/diff is ever returned. This fully satisfies NFR2's real requirement; the divergence from the
   AC's literal 403 is deliberate and flagged for review.
3. **The cache row's `Id` is the `candidateId`** (see ADR 0054); the returned `ImpactPreviewResponse`
   carries it plus `candidateSha256`, `baselineRevisionId`, `baselineCommitSha`, `cacheHit`, `createdAtUtc`,
   the raw unified diff, and the structured summary grouped into `vlanChanges[]`/`portChanges[]` (reusing
   `EntityRefDto` + an `existsInTopology` annotation). A cache hit reconstructs the response from the stored
   row byte-for-byte and serves a strong ETag off the row's content hash.
4. **Baseline projection renders through the same `DesiredStateYamlRenderer` as the candidate** so the raw
   diff is formatting-noise-free. The baseline is the latest ingested revision (via
   `ActiveVersionForRackAsync`); its materialized JSON carries only `switches[].ports[]` (no VLAN
   catalogue), so the projected baseline VLAN catalogue is empty (ADR 0053 scope note).
5. **Audit + metrics are counts/hashes/cacheHit only.** `ImpactPreviewMetrics` (a singleton mirroring
   `PreflightValidationMetrics`) emits an `operation`(compute|cache-hit)/`outcome`-tagged counter and a
   `diff_compute_seconds` histogram; the structured log line carries `rackId`/`baselineRevisionId`/
   `candidateHash`/`cacheHit`/`computeMs`. Neither the audit nor the metrics ever carry the YAML or diff body.

## Consequences

- Invalid YAML and missing baseline both short-circuit before any cache write, so a rejected preview never
  pollutes the cache (AC5).
- Concurrent identical POSTs converge to a single cache row: the loser catches the unique-key conflict and
  re-reads the winner (AC2).
- The 404-vs-403 divergence means an automated test asserting a literal 403 for cross-rack access would
  fail; tests assert 404 + an empty body instead, which is what NFR2 actually requires.
