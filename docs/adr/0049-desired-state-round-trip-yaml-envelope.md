# 0049 — Desired-state round-trip YAML: versioned envelope, canonical ordering, and switchStableKey↔name v1 simplification

## Status

Accepted

## Context

Story #169 adds a safe, deterministic round-trip between the Network Config authoring model (story #168:
a VLAN catalogue plus per-port access-VLAN intents) and an M1 desired-state YAML document. Export must
produce byte-identical, stable-diff YAML (NFR1); import must extract the supported model while preserving
unknown/unsupported sections byte-for-byte (AC2).

The decisive design question was the document schema. The story's AC1 example enumerates top-level keys
`apiVersion, kind, metadata, spec, extensions` and error paths like `spec.vlans[2].vlanId`, and its answered
question mandates a **reserved top-level `extensions` key** as the single preservation anchor. The shipped
git-ingestion pipeline (`DesiredStateValidator`, story #62) instead consumes a legacy flat
`rackSlug`/`switches` shape. Converging git-ingestion onto the new envelope in lockstep would be a large,
higher-risk change, and the story explicitly scopes v1 to "no DB, operate in-memory".

A second question was switch identity: the YAML switch `name` versus the UI `SwitchStableKey`. Joining
against observed-state inventory to translate between a human-friendly name and a stable key adds a
dependency the round-trip does not otherwise need, and without ingestion convergence, **losslessness matters
more than human-friendly names**.

## Decision

Adopt the versioned Kubernetes-style envelope verbatim from AC1 — top-level key order
`[apiVersion, kind, metadata, spec, extensions]` — and encode the entire canonical shape (every key order,
the list sort keys, 2-space indent, LF-only newline, and the reserved `extensions` key) as code constants in
a new `DesiredStateYamlSchema` (Domain), so the renderer, parser, and tests can never drift. All numeric and
length bounds are reused from `DesiredStateSchema` and never redefined. The round-trip envelope
(`DesiredStateRoundTripEnvelope` + `PreservedYamlBlock`) reuses the existing `VlanCatalogueEntry`/
`PortAccessIntent` authoring records; `PreservedYamlBlock` carries a SHA-256 hex checksum of its raw text.
The `spec.switches[].ports[]` field names mirror `DesiredPortIntent` (`name`, `accessVlan`, `description`,
`neighbor{systemName,portId}`) so a future convergence story is cheap. For v1 the UI `SwitchStableKey` is
treated as the YAML switch `name` directly (parse sets `switchStableKey = name`; render emits
`name = switchStableKey`), giving a lossless round-trip without an inventory join.

**Explicit non-goal (v1):** converging the shipped `DesiredStateValidator`/git-ingestion pipeline onto this
envelope. That pipeline keeps its flat shape; this story operates entirely in-memory.

## Consequences

- Two desired-state document shapes coexist temporarily: the legacy flat ingestion shape and the new
  round-trip envelope. The mirrored port field names are the forward-compatibility hook that lets a later
  story converge them without re-deriving the model.
- Because `SwitchStableKey == name` in v1, a switch's YAML name is whatever stable key the authoring model
  holds. When a future story introduces the inventory join (human-friendly names ↔ stable keys), the
  round-trip mapping in the renderer/importer is the single place to change; recorded here as a follow-up.
- The v1 supported model (`VlanCatalogueEntry` + `PortAccessIntent`) carries no port description/neighbor, so
  those port keys are reserved in the ordering constants but never emitted by the renderer and rejected by
  the importer (see ADR 0050) — keeping the v1 round-trip lossless for exactly the authoring model.
- The "renderer, parser, and tests can never drift" guarantee above is made real, not aspirational, in
  [ADR 0051](0051-desired-state-render-slug-validation-and-schema-constant-sourcing.md): the importer's field
  allow-lists are derived from these constants and a renderer guard test pins the emitted key order to them.
