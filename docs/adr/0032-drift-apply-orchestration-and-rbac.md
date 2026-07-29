# 0032 — Drift-apply orchestration and RBAC

## Status

Accepted

## Context

Story #65 exposes Caisson's first WRITE **endpoint**: applying a single, already-computed drift
correction (set access VLAN on one switch port) by driving the story-66 `ISwitchMutatingDriver`. It must
be safe-by-construction: an elevated permission distinct from read/operator viewing; a durable,
idempotent, distributed-safe job so retries or multi-instance scale-out can never double-apply; a
pre-apply revalidation so stale drift is never acted on; and full audit + live status, without
regressing the read-only discovery/drift guardrails (ADR 0012/0013).

Several genuine design choices had to be made, each with more than one defensible answer:

1. **RBAC shape.** A distinct permission could be modelled as (a) reusing `Admin`, (b) a new role-claim
   value alongside the existing `CaissonRoles`, or (c) a parallel permission-claims pipeline separate from
   the existing role-claims transformation.
2. **Concurrency mechanism.** The story's own illustrative example suggested a `RowVersion` column; the
   codebase's proven pattern (`DiscoveryJob`) instead uses the Npgsql `xmin` system column plus a raw-SQL
   atomic claim (`FOR UPDATE SKIP LOCKED`).
3. **Revalidation scope and "staleness" definition.** The drift-item id is a content hash
   (`Diffing.DeterministicGuid`) over rack/type/subject/expected/actual — looking it up by that same hash
   after recompute can only ever say "found, i.e. byte-identical" or "not found"; it can never observe
   "found but changed". The Q3 "Both" requirement (compare presence AND expected/actual) needed a lookup
   that resolves the freshly recomputed report's item **by subject**, not by the original hash.
4. **How the apply path finds the target switch/port** without parsing the deliberately-opaque, versioned
   `SubjectKey` (ADR 0029).
5. **HTTP status codes**, which the story text specified inconsistently across its AC and API sections.
6. **Crash-resume idempotency** for the one device-mutating call per job.

## Decision

1. **RBAC**: a dedicated `CaissonRoles.DriftApply` role-claim value, deliberately excluded from
   `CaissonRoles.All` so holding it alone grants no read access, and so an `Operator` who lacks it is
   rejected (403) exactly as AC1 requires. `RoleClaimsTransformation.ValidateMappings`'s fail-closed
   canonical-target check is widened via a new `CaissonRoles.AllMappableTargets` list (viewing roles +
   `DriftApply`) so an org can map an Entra app-role/group onto it, without adding it to `All`. This reuses
   the codebase's one existing, tested RBAC idiom instead of introducing a second, parallel claims
   pipeline. A `ForbidLoggingAuthorizationResultHandler` decorates the framework's default authorization
   result handler to log a structured warning (subject, path, correlation id) on every Forbidden result —
   generic hardening that satisfies AC1 for every policy, not just this one.
2. **Durable job**: `DriftApplyJob`/`DriftApplyJobStep` mirror `DiscoveryJob`/`DiscoveryJobStep` field-for-
   field — mutable registry rows (not `IAppendOnly`), `UseXminAsConcurrencyToken()`, and a
   `DriftApplyJobRunner` that claims work via the same `UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP
   LOCKED)` pattern as `DiscoveryJobRunner`, over the story's illustrative `RowVersion` example. A
   partial-unique index on `(rack_id, drift_item_id)` filtered to non-terminal statuses DB-enforces "at
   most one active job per drift item", backing an idempotent create: `RequestApplyAsync` never returns a
   hard conflict, only `Created` or `ExistingActiveJob` (the story's answered Q1).
3. **Revalidation**: `DriftApplyJob` captures a `SubjectKey` (the drift item's `DriftSubjectKeys.
   ForSwitchPort` value) at request time, alongside the `ExpectedBeforeVlan`/`ExpectedAfterVlan` anchors
   and the originating `ExpectedDriftReportId`. Revalidation calls the existing
   `IDriftComputationService.ComputeAndPersistAsync` (whole-rack re-diff) and then resolves the LATEST
   report's item **by subject** (a new `DriftQueries.LatestItemBySubjectAsync`), never by the job's
   original content-hashed `DriftItemId` — a hash lookup can only report "identical" or "absent", never
   "present but changed". Absent (port resolved/no longer drifting) or present-with-different-values
   (drifted again, differently) are collapsed into the two Q3 "Both" reason codes (`DriftItemGone` /
   `DriftAnchorsMismatched`), both terminal `StaleDrift`, with the compared report/item ids recorded in
   the job's bounded `ErrorDetailsJson` and in the terminal audit event. No driver call is ever made on
   this path.
4. **Switch/port resolution**: `DriftEngine.BuildPortItem`'s `AccessVlanMismatch` items additively carry
   `{"switchName":...,"portName":...}` in `DriftItem.DetailsJson` (does not change the `DriftItemId` hash,
   which excludes `DetailsJson`). The apply path reads these from the freshly re-resolved item and joins
   `switchName` to `IRackDefinitionProvider`'s `DeviceDefinition.DeviceKey` — the same resolution shape
   `DeviceDiscoveryService` already uses for reads — rather than ever parsing `SubjectKey`.
5. **HTTP contract**: `POST /api/racks/{rackId}/drift/apply` validates in a fixed order — rack access/
   existence (404), malformed/empty `driftItemId` (400), item existence (404), supported type (`Accessv
   lanMismatch` + `Actionable`, else 422 with a `reasonCode`) — before ever calling
   `RequestApplyAsync`, so no job row is ever created on a 400/404/422/401/403 path. A single-field
   `ApplyDriftCorrectionRequest(Guid DriftItemId)` makes "multiple drift items" structurally impossible.
6. **Crash-resume idempotency**: `DriftApplyJob.RecordDeviceOutcome(...)` may be called at most once
   (throws otherwise) and is the sole gate `DeviceApplyAsync` checks before ever calling
   `ISwitchMutatingDriver.SetAccessVlanAsync` — mirrors `DiscoveryOrchestrator`'s `ResultSnapshotId` guard.
   A crash between recording the outcome and the terminal `Complete`/`Fail` transition resumes by
   re-applying the SAME recorded reason code, never re-invoking the driver (AC4/NFR2).
7. **Audit + live status**: reuse the existing append-only `TopologyAuditEvent`/`IAuditEventWriter`
   pipeline (an additive, optional `detailsJson` parameter carries the permission used + correlation id
   on creation, and before/after/reason-code on terminal events) and the existing Redis pub/sub + SignalR
   pipeline (`DriftApplyJobStatusChangedEvent`, rack-scoped via the same `TopologyGroups.ForRack` group —
   no new channel, no hub change).

## Consequences

- The apply path is the first place `Caisson.Orchestration` resolves a device connection outside
  discovery; `Caisson.Api` still names no driver assembly (`Api_references_no_driver_assembly` stays
  green) since only `Caisson.Orchestration` references `Caisson.Drivers.MikroTik`.
- `DriftApplyJob`/`DriftApplyJobDetailDto` are the first entities in the domain model that legitimately
  carry write/remediation-shaped fields (`DesiredVlanId`) and a `Key`-suffixed non-secret identifier
  (`SwitchDeviceKey`/`SubjectKey`) — the M0 domain-guard reflection tests (`DomainGuardTests`) are scoped
  to exempt the new `Caisson.Domain.Drift.Apply` namespace from the remediation-marker sweep, with
  reviewed entries added for the non-secret `Key`-suffixed properties, mirroring the precedent already set
  for `Caisson.Domain.DesiredState`/`Caisson.Domain.Drift`.
- Revalidation re-resolving by subject (rather than trusting `ItemByDriftItemIdAsync`'s cross-report id
  lookup, which is correct for the read API's own purpose — resolving a stable id even across retention —
  but wrong for "is this still current") is a subtle but important distinction; a follow-up story
  extending drift-apply to other drift types must apply the same subject-scoped-to-latest-report pattern,
  not the id-hash lookup.
- This is the first story where an Operator-held role is deliberately insufficient for a write action —
  operators needing to apply corrections must be granted `DriftApply` explicitly (or `Admin`, which
  already satisfies every `RequireRole` policy check via its own canonical role membership only where
  configured — `Admin` does NOT automatically imply `DriftApply` either, by the same "distinct permission"
  design; an org must map or grant `DriftApply` independently even to admins if it wants them to apply
  corrections).
- M1 supports exactly one drift type (`AccessVlanMismatch`); the 422 unsupported-type path and the
  `IsSupported` check in `DriftApplyController` are the single extension point for a future drift type.
