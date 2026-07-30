# 0050 — Unknown-section preservation, comment non-preservation, and the switchStableKey↔name v1 simplification

## Status

Accepted

## Context

Story #169's AC2 requires that importing a desired-state YAML document, editing only supported fields, and
exporting again re-emits any unknown/unsupported sections **byte-for-byte** — no unknown key dropped,
renamed, re-ordered, or reformatted. AC3 requires that YAML comments are explicitly **not** preserved in v1
and that the fact is surfaced as a warning. AC4 requires actionable, fail-fast validation with line/column on
syntax errors and dotted paths on schema/semantic errors, and no partial state on any error. The answered
questions fix three constraints: unknown sections are preserved only under a **reserved top-level
`extensions` key** (not anywhere in the document), schema-invalid supported sections are **rejected
fail-fast**, and the canonical newline is **LF only**.

## Decision

`DesiredStateYamlImporter` reuses the existing bounded, never-throwing DOM load
(`DesiredStateYamlParser.Parse`, which byte-caps the document at `DesiredStateSchema.MaxYamlDocumentBytes`
before YamlDotNet sees it) and then walks the representation-model tree accumulating issues with dotted paths
and a node-count budget. It:

- validates `apiVersion`/`kind`/`metadata.rackSlug`/`spec.vlans`/`spec.switches[].ports[]` into the
  supported model, rejecting any unknown top-level key **except** `extensions` and any unknown key inside
  `spec`/`metadata`/a vlan/a switch/a port;
- runs the shared `NetworkIntentValidator` for semantic rules (VLAN range, duplicate id, duplicate port,
  port referencing an absent VLAN, name/description length) only once the structural walk is clean, mapping
  each validator field back to its YAML path (e.g. `spec.vlans[2].vlanId`);
- **captures the `extensions` block byte-for-byte** by slicing the original source string between the
  `extensions` key's start mark and the next top-level key's start mark (or EOF) — never re-serializing the
  node — into a `PreservedYamlBlock` carrying a SHA-256 checksum. The renderer re-emits it verbatim at the
  canonical last position after verifying the checksum, rejecting a mismatch;
- **detects comments** with a `Scanner` token pass (comments enabled), which is deterministic and robust
  against `#` inside quoted scalars where a regex is not. Any comment **outside** the opaque `extensions`
  bytes raises `commentsNotPreserved`; comments **inside** the `extensions` bytes travel with that block and
  do not warn. Comments are never captured or re-emitted for supported sections;
- returns the full envelope only when there are zero issues; on any error it returns the accumulated issue
  list and **no** model (AC4).

**Comment non-preservation** is intentional for v1: the representation-model DOM discards comment trivia, and
round-tripping comments losslessly would require a comment-aware emitter the "we control the bytes" renderer
(ADR 0025/0049) deliberately does not have.

**switchStableKey ↔ name (v1):** the importer sets `PortAccessIntent.SwitchStableKey = ` the YAML switch
`name`, and the renderer emits `name = SwitchStableKey`, so the round-trip is lossless without an
observed-state inventory join. The v1 supported port model carries only `name` + `accessVlan`; `description`
and `neighbor` are reserved in `DesiredStateYamlSchema.PortKeyOrder`/`NeighborKeyOrder` for a future
convergence story but are rejected by the importer as unsupported port keys today.

## Consequences

- A document whose only "extra" content is comments imports successfully with a `commentsNotPreserved`
  warning; a re-export drops those comments. Operators are told, via the API warning and a persistent UI
  banner, that this is expected in v1.
- Because unknown preservation is anchored solely to top-level `extensions`, unknown constructs placed
  elsewhere are rejected (unknown-key errors), not silently preserved — a deliberate v1 simplification that
  keeps anchoring safe and stable.
- The `extensions` block is opaque bytes: it is the one place non-LF line endings may appear in exported
  output, by design (byte-for-byte preservation wins over LF-normalisation for preserved content).
- Port `description`/`neighbor` present in an imported file are rejected today; converging on the full
  `DesiredPortIntent` shape (and the inventory-join for human-friendly switch names) is the ADR-0049
  follow-up.
