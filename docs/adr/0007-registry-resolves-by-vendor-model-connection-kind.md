# 0007 — Driver registry resolves by (Vendor, Model, ConnectionKind), not the full descriptor

## Status
Accepted

## Context
ADR 0006 introduced `SwitchDriverRegistry`/`BmcDriverRegistry` as plain
`Dictionary<DriverDescriptor, TFactory>` lookups. Because `DriverDescriptor` is a record whose equality
covers all four components — `Vendor`, `Model`, `ConnectionKind` **and** `DriverVersion` — `TryResolve`
did an exact 4-tuple key lookup. That means a caller had to already know a driver's exact
`DriverVersion` to resolve it, which contradicts the intended behaviour ("resolve by
vendor/model/connection-kind") and was flagged in story #3's own PR review. Wiring the first real
driver (MikroTik RouterOS, story #4) surfaced this: discovery code resolves a driver from a device's
vendor/model/connection kind, and has no reason to pin a driver-implementation version.

## Decision
Keep the `Dictionary<DriverDescriptor, TFactory>` and the constructor exactly as-is, so exact-duplicate
detection stays trivial: registering two factories with an **identical** full 4-tuple descriptor still
throws `InvalidOperationException` fail-fast. Change **only** `TryResolve`: instead of an exact-key
lookup it scans the registered factories for entries matching the query's `(Vendor, Model,
ConnectionKind)` and **ignores** the query's `DriverVersion`. When more than one matches, it selects the
highest `DriverVersion` via an in-house `DriverVersionComparer` — segment-wise numeric comparison of the
leading dotted-numeric core (so `1.10.0 > 1.9.0`), a `-prerelease` suffix ranked below the same release
core, and a `StringComparer.Ordinal` fallback for unparseable values. No
`NuGet.Versioning`/SemVer dependency is added, keeping the assembly AOT-compatible and dependency-free
per ADR 0006. The same change is applied symmetrically to `BmcDriverRegistry`.

`DriverDescriptor`'s shape is unchanged — `DriverVersion` remains a component (it is still meaningful for
logging/diagnostics and for registering multiple versions of one driver), so this ADR supersedes only
the *resolution semantics* described in ADR 0006, not the descriptor contract or the registry's
duplicate-detection guarantee.

## Consequences
- Callers resolve a driver by vendor/model/connection-kind without knowing an implementation version;
  registering several versions of the same driver is now legal and the newest wins.
- Resolution is an O(n) scan rather than an O(1) dictionary hit. n is the number of registered drivers
  (single digits in M0), so this is immaterial; if the registry ever grows large it can be indexed by
  the 3-tuple without changing this contract.
- Version ordering lives in one small, unit-tested comparer. If a future driver adopts a richer version
  scheme (build metadata, multi-part prereleases) the comparer is the single place to extend.
- Exact-duplicate detection is preserved: two identical full descriptors are still a programmer error
  and throw at construction.
