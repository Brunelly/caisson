# 0027 — Desired-state revision persistence, derived-current, and read APIs

## Status

Accepted

## Context

Story #63 (M1) is a gap-fill on top of the merged story #62: persist every ingested desired-state
revision per rack with an audit trail, and expose RBAC-protected read APIs for the current desired
state, revision history, and revision-by-id/by-commit lookups. The story's own Database Tables/API
sections propose a new `DesiredStateRevision` table plus a `DesiredStateCurrentPointer` table, and Guid
`/api/racks/{rackId}/desired-state/...` routes — but story #62 already shipped `DesiredStateVersion`
(the append-only per-rack-per-commit envelope, ADR 0025), a derived "current version" query, and a
string-`rackSlug`-keyed `/api/desired-state/racks/{rackSlug}/...` route space, deliberately because no
production path creates an observed-state `Rack` Guid row for every real rack (ADR 0025). Building the
story's literal shape would duplicate #62's schema and 404 for every real rack's Guid-keyed route.

## Decision

- **Extend `DesiredStateVersion` in place; no parallel `DesiredStateRevision` table.**
  `DesiredStateVersion` already IS the revision row story #63 asks for. It gains: nullable
  `AuthorName`/`AuthorEmail`/`AuthorWhenUtc` (AC1 — git may omit committer identity, ingestion still
  succeeds), required `DesiredStateJson` (`jsonb`, the deterministic canonical payload), required
  `SchemaVersion` (bumped via `DesiredStateSchema.CurrentSchemaVersion`), and required `IngestedBy` (the
  fixed `desired-state-ingestion` service-principal identity). All new bounds
  (`MaxAuthorNameLength`/`MaxAuthorEmailLength`/`MaxDesiredStateJsonLength`/`MaxIngestedByLength`) live in
  `DesiredStateSchema`, reused by both the constructor guard and the EF `HasMaxLength` mapping — the same
  double-enforcement precedent ADR 0025 established for `accessVlan`/`description`.
- **"Current" stays derived; no `DesiredStateCurrentPointer`.** A pointer row would be the one mutable
  row in an otherwise append-only schema, reintroducing transactional-reconciliation complexity ADR 0025
  deliberately avoided. `LatestDesiredStateVersionQueries.ActiveVersionForRackAsync`/
  `ActiveVersionWithTreeAsync` (unchanged in shape) already answer "current" via the existing
  `(rack_slug, created_at DESC, id DESC)` covering index, and now carry the payload/author columns for
  free since they select the full `DesiredStateVersion` row.
- **A new `(rack_slug, commit_sha)` index**, not unique (a rack file unchanged since its last ingested
  commit is intentionally skipped — no new row), serves the by-commit lookup and states the per-rack
  SHA-idempotency shape at the DB level. The migration (`AddDesiredStateRevisionMetadata`) is purely
  additive (`ADD COLUMN` / `CREATE INDEX`); no trigger changes — the existing
  `caisson_reject_append_only_mutation`/`_truncate` triggers on `desired_state_version` already cover
  the new columns. New NOT NULL columns get safe defaults (`schema_version` = 1, `desired_state_json` =
  `'{}'`, `ingested_by` = `'pre-story-63'`) so the migration is safe against any pre-#63 dev rows; every
  row the ingestion pipeline writes from here on always supplies real values.
- **String `rackSlug` routing/keying, not Guid `rackId`.** New endpoints stay under
  `/api/desired-state/racks/{rackSlug}/...`, extending the existing story #62 route space
  (`DesiredStateRacksController`) with a sibling `DesiredStateRevisionsController` for
  `revisions` / `revisions/{revisionId}` / `revisions/by-commit/{commitSha}`, all gated by the existing
  `AuthorizationPolicies.TopologyRead` policy (Admin/Operator/ReadOnly/ServiceAccount read; anonymous
  401; unrecognised role 403). A rack slug that fails `DesiredStateSchema.IsValidRackSlug` 404s the same
  way a well-formed-but-nonexistent slug does — slug validity is never an existence oracle.
- **`CursorCodec`/`RequestPaging` gain a string-subject overload.** Desired-state revision history is
  rack-slug-scoped, not Guid-`rackId`-scoped, so the existing HMAC-bound cursor (finding #21) needed a
  sibling `Encode`/`TryDecode`/`ComputeMac` overload keyed on a string subject instead of `Guid rackId`.
  The Guid overloads are unchanged (they now delegate to the same internal core with
  `rackId.ToString("N")` as the subject, so existing cursors decode identically) — a cursor issued for
  one rack slug can never be replayed against another rack's history page.
- **Strong ETag / conditional GET on every payload-returning desired-state read.** `GetActive`,
  `GetRevisionById`, and `GetRevisionByCommit` all set `ETag: "<contentHash>"` and answer a bodyless 304
  when `If-None-Match` already carries it — `ContentHash` is already the tamper-evidence/dedup hash #62
  computes over the raw YAML, so no second hash needs to be introduced for caching. `GetActive`/by-id/
  by-commit 404 with a machine-readable `code` extension (`DESIRED_STATE_NOT_FOUND` /
  `DESIRED_STATE_REVISION_NOT_FOUND`) rather than a bare 404 (AC2/AC3).
- **List views stay metadata-only.** `DesiredStateRevisionQueries.RevisionHistoryPageAsync` projects a
  dedicated `DesiredStateRevisionMetadata` record — never selecting `DesiredStateJson` — so a history
  page never pulls a potentially-large payload column off the wire (AC3, NFR3). By-id/by-commit return
  the full row, and the API re-emits the stored canonical JSON verbatim as a raw `JsonElement` rather
  than reconstructing it from the normalised `DesiredRackIntent`/`DesiredSwitchIntent`/`DesiredPortIntent`
  tables (no extra joins for a payload the row already carries).
- **Ingestion audit event inside the same atomic save.** `DesiredStateIngestionService.ProcessFileAsync`
  adds a `desired-state.revision.ingested` `TopologyAuditEvent` (`ActorType.System`, actor =
  `desired-state-ingestion`, `targetType: "desired-state-version"`, rack slug/commit SHA/content hash in
  the bounded `detailsJson`) to the same change set as the new `DesiredStateVersion`/intent rows, so it
  commits or rolls back atomically with them (mirroring `TopologySnapshotIngestionService`). It sits
  strictly after the unchanged-content skip check, so a replay of an already-ingested commit never
  double-writes an ingestion audit row. Every new read endpoint additionally writes a
  `desired-state.revisions.read` / `desired-state.revision.read` read-audit event via the existing
  `IAuditEventWriter`, matching #62's `desired-state.*.read` action-naming convention.

## Consequences

- **API surface (additive-only, NFR5).** `GET /api/desired-state/racks/{rackSlug}/active` (extended, not
  replaced — no separate `/current` alias) returns `DesiredStateActiveDto` with the typed intent tree
  *and* the raw payload/author fields/ETag. `GET .../revisions?cursor&pageSize` returns a keyset
  `PagedResult<DesiredStateRevisionMetadataDto>`, newest-first. `GET .../revisions/{revisionId:guid}` and
  `GET .../revisions/by-commit/{commitSha}` return `DesiredStateRevisionDetailDto` (metadata + payload),
  each rack-scoped so a revision id/commit belonging to another rack 404s rather than leaking data
  (NFR1). All four endpoints are GET-only; no write/config verb exists under `/api/desired-state/...`.
- A story-#63 reader expecting literal `DesiredStateRevision`/`DesiredStateCurrentPointer` tables or
  Guid-`rackId` routes will not find them; this ADR is the record of that deliberate, reviewed
  divergence, consistent with ADR 0025's own "derive current, don't store a pointer" precedent.
- Because the payload is stored once (on `DesiredStateVersion`) and re-emitted verbatim, a future
  consumer needing the typed intent tree by revision id (not just "active") would need a new
  tree-by-version query mirroring `ActiveVersionWithTreeAsync` — out of scope here since #63 is
  read-only history/lookup, not reconciliation.
