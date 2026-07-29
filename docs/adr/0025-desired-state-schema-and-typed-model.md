# 0025 — Desired-state YAML schema, typed model, and append-only versioning

## Status

Accepted

## Context

Story #62 (M1) needs a constrained, git-backed desired-state format for rack switch-port intent
(access VLAN + optional description + optional neighbor constraint), a way to reject malformed or
out-of-schema input with actionable errors (AC2), and a typed, queryable persistence shape that later
M1 stories (drift detection #64, safe apply) can build on without another migration. It must not
regress M0's read-only observed-state guarantees or reuse the observed-state `Rack` registry in a way
that would block ingestion before an operator has ever registered a rack.

## Decision

- **Constrained schema shape.** One YAML file per rack under `desired-state/racks/*.yaml`
  (AC1/Q1 assumption), each a mapping of `rackSlug` + a list of `switches`, each switch a name + a list
  of `ports`, each port an `accessVlan` (1-4094), optional `description` (length-bounded), and an
  optional `neighbor` constraint (`systemName`/`portId`). No trunk/VXLAN/bonding intent in M1
  (assumption, out of scope). All bounds live in one place, `Caisson.Domain.DesiredState.DesiredStateSchema`
  (`MinVlan`/`MaxVlan`/`MaxDescriptionLength`/`MaxFileBytes`/`MaxFilesPerCommit`/etc.), so the entity
  constructor guards, the hand-written validator, and the EF Core mappings/CHECK constraints can never
  drift from each other.
- **YamlDotNet, loaded as a node DOM, not `Deserialize<T>`.** YamlDotNet is the mainstream, actively
  maintained .NET YAML library. Parsing goes through `YamlStream`/`YamlMappingNode` rather than a direct
  POCO deserialize so `Start`/`End` `Mark`s (line/column) survive into `DesiredStateValidationError`
  (AC2: "parsing error details (file, line, column)"). Unknown fields are rejected explicitly by the
  hand-written schema walk (`DesiredStateValidator`), not by relying on strict-deserialization behaviour
  — this also lets every error accumulate into one list instead of failing on the first problem.
- **Normalised six-table typed model**, matching the story's own Database Tables list:
  `DesiredStateIngestionRun` (mutable run/registry row, modelled on `DiscoveryJob`),
  `DesiredStateVersion` (per-rack-per-commit envelope), `DesiredRackIntent`/`DesiredSwitchIntent`/
  `DesiredPortIntent` (the normalised tree, stable identifiers reusing
  `Topology.Diffing.StableKeys.ForSwitchPort` for ports so later drift/reconciliation work joins against
  observed-state `SwitchPort` rows using the same identity scheme), and `DesiredStateValidationError`.
  `rackSlug` is a plain string on every one of these — deliberately **not** an FK to the observed-state
  `Rack.Id` — because no production path creates `Rack` registry rows today and desired-state ingestion
  must not be blocked on that.
- **Append-only versioning with a derived active version.** `DesiredStateVersion` and the three intent
  entities and `DesiredStateValidationError` all implement `IAppendOnly`: once inserted, a version's rows
  are never updated (NFR7's "no updates to historical rows" holds via the existing `GuardAppendOnly()`
  DbContext check for free). "The active version for a rack" is always DERIVED — the newest version row
  per `rackSlug`, ordered `created_at DESC, id DESC` (the same deterministic tie-break ADR 0002
  established for observed-state snapshots) — never a mutated flag. `DesiredStateVersion.IsActive` is a
  write-once breadcrumb, always `true` at insert, and is never read directly to answer "what's active";
  all such reads go through `LatestDesiredStateVersionQueries`. `DesiredStateIngestionRun` is
  deliberately **not** append-only — like `DiscoveryJob`, it is a mutable registry row whose `Status`
  transitions in place as the run progresses.
- **Double-enforced bounds.** `accessVlan`'s range and `description`'s length are checked both in the
  `DesiredPortIntent` constructor and by a PostgreSQL `CHECK` constraint added in
  `DesiredPortIntentConfiguration` — the same double-enforcement ADR 0004 established for
  `ConfidenceScore`, so the invariant holds even against direct SQL writes.

## Consequences

- Partial-accept (Q3) is a natural consequence of per-rack, append-only versioning: a commit that
  invalidates one rack file simply produces no new version (plus validation-error rows) for that rack,
  while every other rack's newest-row-wins query is untouched — no special-casing needed at the query
  layer.
- `Caisson.Domain.Tests.DomainGuardTests`'s existing "no remediation/desired-state fields" sweep is a
  hard M0-only guardrail (see `CLAUDE.md`); it is now narrowed to exclude the
  `Caisson.Domain.DesiredState` namespace (which legitimately has "Intent"/"Desired"-named types), with a
  sibling `DesiredStateGuardTests` asserting the invariants that DO still apply to it: no
  credential/secret-shaped field, and no hardware-write/apply/reconcile-shaped method, keeping this
  story's read-only boundary compile-time checkable.
- Retention is "keep all history indefinitely" (Q2); no cleanup job exists yet. If that changes, it is a
  separate, additive story — append-only tables make a later time-boxed retention job straightforward to
  add without touching the ingestion/query code paths.
- A rack file with no prior valid version and a validation failure has no active version at all (not
  even a placeholder) — callers of the active-desired-state API must handle "no active version yet" as
  a normal 404, not an error.
