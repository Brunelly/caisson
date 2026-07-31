# 0052 — Pre-flight validation for network-config authoring

## Status

Accepted

## Context

Story #170 adds a pre-flight validation step to the network-config authoring workflow so invalid or unsafe
desired-state changes are caught before a Git PR is created: schema validation against the existing M1
desired-state schema, semantic validation against the rack's observed topology, and non-blocking safety
warnings for changes to management/uplink ports. Several forces shaped the design:

- Errors must be actionable and mapped to specific UI fields with **stable identifiers** (NFR1), grouped
  by severity, with no stack traces or 500s for validation failures.
- Validation must be **deterministic and side-effect free** — repeated calls yield identical issue sets
  and no DB writes except audit logs (NFR3/NFR4).
- Warning acknowledgement must be **TOCTOU-safe**: a PR must not be created against a candidate/topology
  that changed after the user saw the warnings (story Q3).
- The rule set must be maintainable and must **not duplicate** the schema/semantic rules the authoring
  save path already uses (NFR5).
- There is no existing Git-write / PR-creation path in the codebase.

## Decision

1. **RFC 6901 JSON Pointer is the canonical `fieldPath`**, with a lightweight `uiPath` (bracket/dot form,
   e.g. `vlanCatalogue.vlans[2].id`, `ports["switchA/ether5"].accessVlanId`) alongside it for the Angular
   editor to map an issue to a control (story Q1). A `JsonPointer` helper escapes `~`/`/` per §3.
2. **The `validationRunId` is a stateless, content-bound SHA-256** over
   `rackId + canonicalized(vlanCatalogue, portIntents) + observedSnapshotId` (`ValidationRunToken`). No DB
   row, expiry, or signature machinery — the PR endpoint independently re-runs validation and re-derives
   the id, so a hash is sufficient for TOCTOU safety and honours NFR3 (no writes). Chosen over a
   Data-Protection-encrypted receipt because the re-validation makes actor/expiry/tamper machinery add
   complexity without gating value.
3. **Schema/semantic rules are reused, not re-implemented.** `PreflightValidator` calls the shared
   `NetworkIntentValidator` (which sources M1 bounds from `DesiredStateSchema`) verbatim and translates
   each `(field, message)` onto a field-addressable `PreflightIssue` via one mapping table — no second
   rule set. This validates the *structured authoring model* directly (rather than rendering YAML then
   re-parsing), which maps issues to UI fields robustly and avoids a legacy-vs-authoring schema mismatch.
   Note the reused validator reports each *duplicate* VLAN/port occurrence (the 2nd+), not the first
   surviving entry — a deliberate consequence of the no-duplication constraint.
4. **Port role classification is heuristic-derived and reuses `Caisson.Correlation`.** No explicit port
   role field exists in M0/M1 observed state. The trunk/uplink rule + threshold + token normalization were
   extracted from the internal correlation `SnapshotIndex` into a new **public** `PortRoleClassifier` in
   `Caisson.Correlation`, which `SnapshotIndex` now delegates to (one rule, no drift). The Infrastructure
   `RackInventoryProjector` reuses it against the persisted snapshot and composes a `management` signal on
   top (reserved port name / LLDP management address matching the switch management IP). Domain stays pure:
   the role is pre-computed in Infrastructure and carried onto `InventoryPort`, so the Domain safety rule
   reads a role rather than a heuristic. Learned-MAC-per-port counts are not persisted, so that fallback
   trunk signal is unavailable post-persistence; the persisted LLDP-peer-switch and multi-tag signals drive
   the classification.
5. **PR creation is a stubbed seam.** No git-write path exists, so an `IDesiredStatePrService` seam is
   introduced with a shipped `NotYetEnabledDesiredStatePrService` that performs no git write and returns a
   synthetic gate-passed result. The controller fully enforces and audits the gate (server re-validation,
   run-id match, warning acknowledgement); the real forge/PR pipeline is deferred to **#172**. The
   deliberately read-only `LibGit2SharpRepositoryProvider` is **not** repurposed for writes.

## Consequences

- Two new NetworkConfigAuthor-gated endpoints (`POST .../desired-state/preflight-validate`, `POST
  .../desired-state/prs`), a `PreflightValidationMetrics` counter+duration histogram for the NFR2
  P95 ≤ 500ms target, and `desired-state.preflight-validated` / `pr-created` / `pr-rejected` audits with
  counts/outcome/snapshotId only. Both endpoints are side-effect free except the audit write and are added
  to the `ReadOnlyGuardTests` non-GET allow-list.
- Adding future rules follows [docs/adding-a-validation-rule.md](../adding-a-validation-rule.md): a stable
  `PreflightCodes` code, a JSON-Pointer field path, an `EntityRef`, deterministic ordering, and per-rule
  tests.
- Making `PortRoleClassifier` public is a small, permanent surface addition to `Caisson.Correlation`; its
  behaviour is locked by both the correlation tests (unchanged) and new classifier/projector tests.
- The PR endpoint returns `202 Accepted` with a null `pullRequestUrl` until #172 lands; clients must treat
  a gate-passed response as "accepted for PR creation", not "PR exists".
