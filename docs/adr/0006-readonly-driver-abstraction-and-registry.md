# 0006 — Read-only driver abstraction and registry

## Status
Accepted

## Context
M0 discovery needs pluggable, vendor-specific drivers for switches (starting with MikroTik) and BMCs
(Redfish/IPMI), but this story is abstraction-only — concrete implementations are separate stories
(#4/#5). The abstraction must (1) make a compile-time-visible safety boundary so no driver interface
can ever expose a write/power/configure operation, (2) give callers a structured way to represent
partial success and common failure modes without throwing for expected failures, (3) let connection
configuration be supplied once per driver instance rather than on every call, (4) let business logic
resolve a driver by vendor/model/connection-kind without depending on a concrete vendor type, and (5)
stay reflection-free so it can be shared with the future NativeAOT appliance agent (NFR4).

## Decision
- Add `Caisson.Drivers.Abstractions`, layered like `Caisson.Domain` (ADR 0001): `IsAotCompatible`,
  zero EF Core/Npgsql references, and only a `ProjectReference` to `Caisson.Domain` plus
  `Microsoft.Extensions.DependencyInjection.Abstractions` — the interfaces-only DI package, not the
  concrete container, to keep the abstraction reflection/codegen-free.
- **Read-only safety boundary**: `ISwitchDiscoveryDriver`/`IBmcDiscoveryDriver` live in the
  `Caisson.Drivers.Abstractions.ReadOnly` namespace, visible directly from the `using` statement, not
  just enforced by convention. Every method returns `Task<DriverResult<T>>` and accepts a single
  `CancellationToken`. A reflection-based test (`SafetyBoundaryGuardTests`, mirroring the existing
  `DomainGuardTests` pattern) scans every method name in that namespace for mutation verbs (Set,
  Update, Create, Delete, Reset, PowerCycle, ...) and fails the build if one appears (NFR1).
- **Two-tier error/diagnostic model**, instead of forcing everything into one enum:
  - `DriverErrorCode` is a *new* enum for call-level failures (timeout, connection refused, device
    unreachable, auth failed/denied, protocol error, parse error, unsupported operation). It is new
    because `Caisson.Domain.Enums.ReasonCode` has no timeout/connection-refused/protocol-error
    concepts — it exists to annotate correlation ambiguity, not driver-call failure.
  - `DriverDiagnostic` reuses `ReasonCode` directly (not a parallel enum) for per-item annotations on
    an otherwise-successful result, e.g. one switch port with `MissingLldp`. This is the same
    ambiguity vocabulary already used by `TopologyCandidateMapping`, so a "device unreachable" or
    "parse error" reason means the same thing whether it was recorded during correlation or during
    discovery.
  - `DriverResult<T>` carries `Success`, `Value` (non-null iff success), `Error` (non-null iff
    failure), `Diagnostics` (can be non-empty even when `Success` is true — the partial-LLDP case),
    and `Duration`. Constructible only via `Ok`/`Fail` factories, mirroring the validate-at-
    construction style of `MacAddressValue`/`ConfidenceScore` (ADR 0004).
- **Cancellation is caller-initiated control flow, not a device-reported failure.** There is
  deliberately no `DriverErrorCode` value for cancellation: an already-cancelled `CancellationToken`
  must throw the standard `OperationCanceledException` per BCL convention, not return a
  `DriverResult`. Locked in by `CancellationPropagationTests` so stories #4/#5 don't invent a second
  convention.
- **Factory-encapsulated connection config.** `SwitchConnectionOptions`/`BmcConnectionOptions` are
  bound into a driver instance by `ISwitchDriverFactory`/`IBmcDriverFactory` at creation time, not
  passed per call. They carry a `CredentialsRef` — an opaque reference/name to a secret-store entry —
  never a raw secret. Real credential/secret-store wiring is explicitly out of scope here and belongs
  to the concrete driver stories.
- **Non-reflective registry.** `SwitchDriverRegistry`/`BmcDriverRegistry` are plain
  `Dictionary<DriverDescriptor, TFactory>` lookups built from whatever `ISwitchDriverFactory`/
  `IBmcDriverFactory` instances the DI container already has (`AddCaissonDriverRegistry()`,
  `AddSwitchDriver<T>()`, `AddBmcDriver<T>()`). No reflection, dynamic proxies, or assembly scanning.
  A duplicate `DriverDescriptor` throws `InvalidOperationException` at registry construction
  (fail-fast, matching the codebase's validate-at-construction convention). Chosen over .NET 8 keyed
  DI services because a plain dictionary is trivially unit-testable without a `ServiceProvider` and
  keeps the door open for a future NativeAOT agent that may not use
  `Microsoft.Extensions.DependencyInjection` at all.
- Interfaces are named `ISwitchDiscoveryDriver`/`IBmcDiscoveryDriver` rather than the story's
  illustrative `ISwitchDriver`/`IBmcDriver`, because the task's binding "Key Area to Focus On" names
  the `*DiscoveryDriver` spelling explicitly, and the naming leaves room for a future
  `*MutatingDriver` pair without a rename.
- `IBmcDiscoveryDriver.GetNetworkInterfacesAsync` alone satisfies the BMC NIC-MAC-inventory
  requirement — there is no separate `GetEthernetMacsAsync`, since each returned
  `BmcNetworkInterfaceInfo` already carries its own MAC and a second API could disagree with the
  first about a BMC's NIC list.

## Consequences
- The safety boundary is enforced twice: structurally (the `ReadOnly` namespace and the absence of
  any write method) and by an automated guard test that fails CI if that ever regresses.
- Downstream discovery code gets one consistent way to log/retry driver failures
  (`DriverErrorCode` + `Retryable`) and one consistent way to represent partial reads
  (`Diagnostics` + `ReasonCode`), without inventing a second taxonomy per vendor.
- Stories #4/#5 (MikroTik/Redfish) implement `ISwitchDiscoveryDriver`/`IBmcDiscoveryDriver` and a
  matching factory; they do not need to revisit the error model, cancellation convention, or registry
  shape — those are settled here.
- Real credential handling (secret resolution behind `CredentialsRef`) is deferred; stories #4/#5
  must design that without changing the shape of `SwitchConnectionOptions`/`BmcConnectionOptions`
  here, or this ADR will need a follow-up.
