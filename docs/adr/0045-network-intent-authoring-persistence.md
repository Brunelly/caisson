# 0045 — RackNetworkIntent as a new mutable, xmin-concurrent persistence model

## Status

Accepted

## Context

Story #168 needs a rack-scoped place to save authored VLAN-catalogue and per-port access-VLAN intent
from the UI, ahead of the future #169 YAML-generation/#171 diff/#172 PR pipeline. The obvious-looking
reuse would be the existing `Caisson.Domain.DesiredState` tree (`DesiredRackIntent`/`DesiredSwitchIntent`/
`DesiredPortIntent`) — but that tree is deliberately **append-only**, fed exclusively by the git-ingestion
pipeline (story #62), and the `CaissonDbContext` guard actively rejects any update/delete against it
(NFR4, tamper-evidence). Interactive authoring is fundamentally the opposite shape: a user edits a VLAN
name, retires an entry, or clears a port intent, and expects the SAME row to change in place — a
draft-first workflow, not a new immutable revision per edit. The story's own Q3 answer ("single saved
state only — no draft/publish, no version history") also rules out reusing the versioned
`DesiredStateVersion` concept.

A second question was how to shape optimistic concurrency for the save endpoint. The story's
illustrative data-model text suggested a hand-rolled `version` int column, but the codebase's proven
precedent for exactly this need (`DriftApplyJob`, story #65) instead maps the row's Postgres `xmin`
system column via EF Core's `UseXminAsConcurrencyToken()` — no schema column, no manual increment logic,
and the database itself is the source of truth for "has this row changed since I read it".

## Decision

`RackNetworkIntent` is a **new**, mutable entity in a fresh `Caisson.Domain.NetworkConfig` namespace,
sibling to (never part of) `Caisson.Domain.DesiredState`. It holds exactly one row per rack (a unique
index on `RackId`, matching the single-saved-state answer), storing the catalogue + port intents as one
bounded `jsonb` payload rather than normalizing into per-VLAN/per-port tables — the story explicitly
allows this for V1, and it keeps the whole draft transactionally atomic on save. Concurrency is the row's
`xmin`, surfaced to the API as a weak `ETag`/`If-Match` pair (mirroring HTTP's own conditional-request
semantics) rather than any bespoke version field. A shared, EF-free `NetworkIntentValidator` reuses
`DesiredStateSchema`'s VLAN-range/description-length bounds directly, so authoring and the future YAML
pipeline can never define "a valid VLAN" differently.

## Consequences

- The domain/secret-marker reflection guard tests (`DomainGuardTests`) needed a new namespace exemption:
  `Caisson.Domain.NetworkConfig` legitimately carries "Intent"-named fields (that is its entire purpose),
  the same carve-out `DriftApply`/`DesiredState` already have.
- Because there is no version history, a future story adding draft/publish or multi-version support will
  need a genuinely new model, not an incremental extension of this one — an accepted, explicit trade-off
  for shipping V1 quickly (story's own assumption).
- Any future consumer (the #169 YAML generator) reads `RackNetworkIntent.IntentJson` directly rather than
  joining a normalized schema; if the payload grows enough to need per-VLAN referential integrity at the
  database level, that will require a follow-up migration to normalized tables.
