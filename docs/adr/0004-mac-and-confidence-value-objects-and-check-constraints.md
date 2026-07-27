# 0004 — MAC and Confidence value objects with DB CHECK constraints

## Status
Accepted

## Context
Two domain invariants must hold consistently across every source of observations. (1) MAC addresses
arrive from BMC inventory and switch tables in varied formats (`:` / `-` / `.` grouped, bare, mixed
case) and must be stored in one canonical form so equality and indexing work. (2) Correlation
confidence must be bounded to `[0.0, 1.0]`; an out-of-range or `NaN` score is a bug that should never
reach the database.

## Decision
Introduce two `readonly record struct` value objects in `Caisson.Domain`:
- `MacAddressValue` — `Parse`/`TryParse` accept any common input format and normalize to **lowercase,
  12-hex, no separators**; invalid length/hex is rejected; `ToDisplay()` returns the colon-grouped
  form. Stored via an EF value converter as the normalized string.
- `ConfidenceScore` — factory validates `[0.0, 1.0]` and rejects `NaN`; stored as `double`.

Enforce the confidence bound **twice**: in the value-object factory (application level) and again with
a PostgreSQL `CHECK (confidence >= 0.0 AND confidence <= 1.0)` constraint on the mapping table, so the
invariant holds even against direct SQL writes.

To avoid a name clash between the value object and the observed `MacAddress` entity (table
`mac_address`), the entity exposes its normalized value through a `Mac` property of type
`MacAddressValue`.

## Consequences
- One canonical MAC representation everywhere; equality/joins/indexes are reliable; display formatting
  stays a presentation concern.
- Confidence is defended in depth (code + database); malformed scores cannot be persisted.
- Value objects require EF value converters (in Infrastructure), keeping Domain persistence-ignorant.
