# 0053 — Desired-state impact-preview diff engine

## Status

Accepted

## Context

Story #171 adds an impact-preview step to the network-config authoring workflow: the server computes a diff
between a rack's latest ingested desired-state revision (baseline) and a candidate YAML, returning both a
raw unified text diff and a structured, human-readable summary of semantic changes (VLANs added/removed/
modified, per-port access-VLAN changes). Several forces shaped the diff engine design:

- The diff must be **deterministic** — repeated calls with identical baseline/candidate content yield
  byte-identical raw diff and identically-ordered structured summary (NFR3), so reviews and audits are
  stable.
- The structured summary must carry **stable identifiers** for each changed entity so the UI can deep-link
  into the topology view, and must read verbatim like the story's examples (`VLAN 100 added`,
  `Switch sw1 Port ether3 accessVlan changed 10→20`, `VLAN 20 name changed 'corp'→'prod'`).
- The technical constraints say to **avoid reflection-heavy libraries** in shared agent components and to
  **keep diff logic in shared domain models where possible**, so the engine must stay AOT-clean and
  dependency-free.
- The answered design question fixes the raw diff to run over **canonicalized YAML** (reduced noise for
  reviews), not the user's original text.

## Decision

1. **Raw diff via a hand-rolled, in-domain LCS unified-diff formatter** (`UnifiedDiffFormatter`), not a
   NuGet diff library (e.g. DiffPlex). Canonical rack YAML is small (hundreds of lines), so the O(n·m) LCS
   is safe, and hand-rolling it avoids a new dependency + licence audit and honours the "avoid
   reflection-heavy libraries / keep diff logic in shared domain" constraint while staying AOT-clean and
   fully deterministic. Emits standard `@@ -a,b +c,d @@` hunks with `+`/`-`/space prefixes and configurable
   context; identical inputs yield an empty diff.
2. **Structured summary via a hand-rolled deterministic `SemanticDiffEngine`.** VLANs are compared by id,
   access-port intents by `(switchStableKey, portName)`. Each change reuses the existing
   `NetworkConfig.Preflight.EntityRef` for topology deep-linking and carries a stable `ChangeId` derived
   with the same SHA-256/first-16-bytes discipline as `Drift.Diffing.DeterministicGuid`. Ordering is fully
   deterministic: VLAN changes precede port changes; VLANs by id ascending; ports by the ordinal escaped
   `(switchStableKey, portName)` key (`StableKeys.EscapeSegment`).
3. **Both baseline and candidate render through the same `DesiredStateYamlRenderer`** before diffing, so the
   canonical YAML is symmetric and the raw diff carries zero formatting noise. The content hash for cache
   keying reuses `ValidationRunToken`'s canonicalize → length-prefix → SHA-256 → 64-hex discipline
   (`DesiredStateContentHash`).

## Consequences

- The diff engine is pure (no EF/IO/reflection) and lives in `Caisson.Domain/DesiredState/Diffing/`, so it
  is shareable with the future appliance agent and unit-testable without a database.
- **Port-description scope exclusion:** `PortAccessIntent` has no description field in the M1 supported
  model, so "port description changes if present" (AC1) is out of the *semantic-summary* scope. Description
  changes still surface in the *raw unified diff*. If the supported model later gains a port description,
  the semantic engine gains a `Modified` clause for it — no cache-key change required.
- The hand-rolled LCS is O(n·m) in lines; acceptable for the bounded canonical-YAML sizes here but not a
  general-purpose diff for arbitrarily large inputs.
