# 0029 — Versioned drift subject keys and scoped `DriftItemId` uniqueness

## Status

Accepted

## Context

Story #64's own answered question requires drift subject keys to be a "versioned composite key that
prefers natural identifiers ... and falls back to DB IDs". The observed-state model already has a
canonical natural-key scheme, `Topology.Diffing.StableKeys` (ADR 0011), and the desired-state model
already stores its own `StableKey` per node (`DesiredPortIntent.StableKey`, ADR 0025) computed via
`StableKeys.ForSwitchPort`. Reusing either verbatim for drift is tempting but wrong: the desired-side key
is derived from the git-ingested `rackSlug` string, while the observed-side key is derived from the
trusted config `deviceKey` plus the device's reported serial/management IP — the two are not
string-comparable even when they describe the same real switch port, because nothing today guarantees
`rackSlug == deviceKey` or that the desired and observed pipelines were fed the same identifying
attributes. A drift engine that joined on `StableKey` string equality would silently fail to match any
real rack unless that coincidence held.

## Decision

- **`DriftEngine` joins desired and observed state on natural attributes, not on `StableKey`:**
  `DesiredStateVersion.RackSlug == Rack.ExternalKey`, `DesiredSwitchIntent.SwitchName ==
  Switch.ExternalDeviceKey`, `DesiredPortIntent.PortName == SwitchPort.PortName` — attributes both sides
  actually share once a rack's `ExternalKey`/`ExternalDeviceKey` are set up to alias the desired-state
  slugs/names for that rack (an operational/deployment concern, not a drift-engine one).
- **A NEW, versioned subject-key scheme, `Drift.Diffing.DriftSubjectKeys`**, re-keys the JOINED result for
  persistence: `"v1|{rackKey}|{switchName}|{portName}"` for a switch port,
  `"v1|{rackKey}|{nicMac}"` for a server NIC. The leading `v1` schema-version segment lets a future key
  format change without colliding with keys already persisted under this one. Each free-form segment is
  escaped via `Topology.Diffing.StableKeys.EscapeSegment` (made `internal`, not duplicated) — the same
  `|`/`%` percent-encoding defence that prevents two different `(component-set, values)` pairs from
  colliding onto the same composite key.
- **`DriftItemId` is computed by `Drift.Diffing.DeterministicGuid`**: SHA-256 over
  `rackId|driftType|subjectType|subjectKey|expectedValue|actualValue` (UTF-8, `|`-joined, free-form
  segments escaped), truncated to its first 16 bytes and reinterpreted as a `Guid`. This is deliberately
  pure and stateless — the same finding, expressed against the same subject with the same expected/actual
  values, always hashes to the same id, which is what lets a recompute upsert by id (AC3) rather than
  minting a fresh row every time.
- **The hash formula deliberately excludes the desired-revision/observed-snapshot identity.** A
  consequence, not an oversight: the identical real-world drift (same rack, type, subject, expected,
  actual) computed against two different revision/snapshot pairs — e.g. an unrelated port's drift is fixed
  between two revisions, but this one persists — hashes to the SAME `DriftItemId` in both reports. If
  `DriftItemId` were globally unique, the second report's insert would fail outright. Story #64's own
  example formula does not include revision/snapshot, and mirrors `TopologyEntityDiff`'s established
  precedent of scoping uniqueness to the row's own container (there, `snapshot_id`; here, `DriftReportId`)
  rather than making a content hash carry uniqueness alone.
- **Consequently, `DriftItem` uses a surrogate PK (`Id`) plus the content-hashed `DriftItemId`, with a
  UNIQUE index scoped to `(DriftReportId, DriftItemId)`** — see ADR 0028. `GET .../drift/items/{driftItemId}`
  resolves the *latest* report containing that id (`DriftQueries.ItemByDriftItemIdAsync`), since the same
  id may legitimately appear in more than one report.

## Consequences

- A future drift rule that needs a subject the current scheme cannot express (e.g. a LAG or a
  multi-switch logical link) requires a new `DriftSubjectKeys.For*` method and, if the format itself must
  change, a `v2` prefix — not a breaking change to already-persisted `v1` keys.
- Because natural-key joining depends on `Rack.ExternalKey`/`Switch.ExternalDeviceKey` aligning with the
  desired-state `rackSlug`/`switchName`, a rack whose observed and desired identifiers were configured
  inconsistently will silently produce `MissingDesiredEntity`/`ExtraObservedEntity` drift for its entire
  port set rather than a clear "identifiers don't match" diagnostic. This divergence risk is accepted for
  M1; a future story could add an explicit alignment check if it proves to be an operational footgun.
- `DriftItemId` collisions across unrelated subjects are cryptographically negligible (SHA-256), and the
  segment-escaping defence rules out the deliberate-collision class the observed-state stable keys already
  defend against (finding #3's precedent).
