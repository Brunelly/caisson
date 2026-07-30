# 0051 — Desired-state render: server-authoritative rackSlug validation, and schema-constant sourcing

## Status

Accepted

## Context

Two round-trip invariants from story #169 were under-enforced after ADR 0049/0050 shipped:

1. **The rendered document must be re-parseable (AC1 schema-conformance, AC2 export→re-import).** The render
   endpoint derives `metadata.rackSlug` from the target rack's `Rack.ExternalKey`, which is only length-bounded
   (`<=256` arbitrary chars). The importer, however, enforces `DesiredStateSchema.IsValidRackSlug` (a DNS-label
   of `<=64` chars). A rack whose `ExternalKey` is not slug-shaped (uppercase, `_`, `.`, or over 64 chars) would
   therefore render a document the same system's parser rejects — a data-dependent break invisible to tests that
   round-trip only slug-shaped keys. The git-ingestion path already treats the two as one concept (it resolves a
   rack by `ExternalKey == rackSlug`), so a non-slug `ExternalKey` is a genuine anomaly, not a supported shape.

2. **ADR 0049 claimed the renderer/parser "can never drift" because every key order lives in
   `DesiredStateYamlSchema`.** In practice the importer defined its own parallel allow-list `HashSet`s and the
   hand-written renderer emitted keys as literals, so nothing read those constants — the guarantee was
   aspirational.

## Decision

- **Validate `metadata.rackSlug` on render.** `DesiredStateYamlRenderer.Render` now runs the same
  `DesiredStateSchema.IsValidRackSlug` predicate the importer uses and throws `DesiredStateRenderException`
  (surfaced as a 400 with a `metadata.rackSlug` path) when the rack's `ExternalKey` is not a valid slug. We
  **reject rather than normalize**: normalization would be lossy, could collide across racks, and would need its
  own reversible-mapping design; rejecting keeps the renderer's existing "never emit an invalid document"
  guarantee honest and is trivially reversible if a future story introduces a real slug-derivation.
- **Source the importer's field allow-lists from the schema constants.** `DesiredStateYamlImporter`'s
  `RootKeys`/`MetadataKeys`/`SpecKeys`/`VlanKeys`/`SwitchKeys`/`PortKeys` are now derived from
  `DesiredStateYamlSchema.{TopLevel,Metadata,Spec,Vlan,Switch,SupportedPort}KeyOrder`, and a renderer guard test
  pins the emitted key order at every level to the same lists. A new `SupportedPortKeyOrder` names the v1 port
  prefix (`name`, `accessVlan`); `description`/`neighbor` remain the reserved tail of `PortKeyOrder`.
- **Reject multi-document YAML** in the shared `DesiredStateYamlParser` (a `---`-separated stream previously kept
  only the first document silently), with an actionable error and the second document's line/column (AC4). This
  hardens both the round-trip and git-ingestion paths.

## Consequences

- A rack whose `ExternalKey` is not slug-shaped cannot be exported to desired-state YAML until its key is made
  DNS-label-shaped; the operator gets a clear 400 rather than a silently unparseable file. `rackSlug` remains
  server-authoritative and is not cross-checked against any imported value (a YAML authored for another rack is
  accepted and re-slugged on export), so import→export is identical for every field except `metadata.rackSlug`.
- The ADR-0049 "cannot drift" guarantee is now real: reordering the emitter or changing an allow-list without
  updating `DesiredStateYamlSchema` fails the guard/schema-consistency tests.
- Multi-document desired-state input is now a fail-fast error everywhere, not a lossy silent truncation.
