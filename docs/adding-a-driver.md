# Adding a driver

This walks through adding a new read-only discovery driver (e.g. a MikroTik switch driver or a
Redfish BMC driver) on top of `Caisson.Drivers.Abstractions`. See
[ADR 0006](adr/0006-readonly-driver-abstraction-and-registry.md) for the design decisions behind the
shapes described here.

## 1. Implement the driver interface

Implement `Caisson.Drivers.Abstractions.ReadOnly.ISwitchDiscoveryDriver` (switches) or
`IBmcDiscoveryDriver` (BMCs) in your own project, referencing `Caisson.Drivers.Abstractions`.

- Every method returns `Task<DriverResult<T>>` and accepts a `CancellationToken`.
- **Never throw for an expected failure** — an unreachable device, bad credentials, or a timeout must
  come back as `DriverResult<T>.Fail(new DriverError(code, message, retryable), duration)`, not an
  exception. The only exception that should propagate out of a driver method is
  `OperationCanceledException` when the caller cancels (see
  `tests/Caisson.Drivers.Abstractions.Tests/CancellationPropagationTests.cs` for the expected
  behaviour — do not invent a `DriverErrorCode` for cancellation).
- For a call that partially succeeds (e.g. LLDP is disabled on some ports), return
  `DriverResult<T>.Ok(value, duration, diagnostics)` with one `DriverDiagnostic` per affected item,
  using `Caisson.Domain.Enums.ReasonCode` to describe why (`MissingLldp`, `DeviceUnreachable`,
  `ParseError`, ...). `Success` stays `true`.
- `DriverError.Message` must never contain credential/secret material, even for an
  `AuthenticationFailed` result.
- See `tests/Caisson.Drivers.Abstractions.Tests/ExpectedFailureSemanticsTests.cs` for the exact
  Given/When/Then scenarios (auth failure → `AuthenticationFailed`/`Retryable=false`; timeout →
  `ConnectionTimeout`/`Retryable=true`) your driver's behaviour is expected to match.

## 2. Implement a matching factory

Implement `ISwitchDriverFactory`/`IBmcDriverFactory`. The factory owns a `DriverDescriptor` (vendor,
model, connection kind, driver version) and binds a `SwitchConnectionOptions`/`BmcConnectionOptions`
value into a driver instance in `Create(options)` — connection config is supplied once per driver
instance, not passed to every call. `CredentialsRef` on the connection options is a reference into
whatever secret store you use, never the raw secret; resolving it into real credentials is your
driver's responsibility, not this abstraction's.

## 3. Register it

```csharp
services.AddSwitchDriver<MyMikroTikSwitchDriverFactory>();
// or: services.AddBmcDriver<MyRedfishBmcDriverFactory>();
services.AddCaissonDriverRegistry();
```

Business logic then resolves your driver without referencing its concrete type:

```csharp
if (switchDriverRegistry.TryResolve(descriptor, out var factory))
{
    var driver = factory.Create(connectionOptions);
}
```

## 4. Copy the reference implementation

`tests/Caisson.Drivers.Abstractions.Tests/Mocks/` (`MockSwitchDiscoveryDriver`,
`MockBmcDiscoveryDriver`, `MockSwitchDriverFactory`, `MockBmcDriverFactory`) is a working, minimal
implementation of every interface described above and a reasonable starting point to copy from.

## 5. Don't add a mutating method

`SafetyBoundaryGuardTests` (in the same test project) reflects over every interface in the
`Caisson.Drivers.Abstractions.ReadOnly` namespace and fails the build if any method name contains a
mutation verb (`Set`, `Update`, `Create`, `Delete`, `Reset`, `PowerCycle`, ...). Write operations
belong in a future `*Mutating` interface, not here.

## 6. A worked example: the MikroTik RouterOS driver

`Caisson.Drivers.MikroTik` is the first production driver and a concrete example of every step above —
an internal BCL-only RouterOS API client with a code-level read-only command allowlist, tolerant
v6↔v7 parsing, an env-backed `ISwitchCredentialResolver` for `CredentialsRef`, and a
simulator-backed integration suite. See [docs/routeros-discovery.md](routeros-discovery.md) for its
data sources, least-privilege RouterOS permissions, and how to run its tests (simulator or real CHR),
and [ADR 0008](adr/0008-mikrotik-routeros-readonly-driver.md) for the design decisions.

## 7. A second worked example: the Redfish / IPMI BMC driver

`Caisson.Drivers.Redfish` is the first production **BMC** driver and shows the same steps for an
`IBmcDiscoveryDriver`: a BCL-only `HttpClient` Redfish client with a code-level read-only **path**
allowlist (`RedfishReadPaths` — GET-only, no `/Actions/` or `/Settings`), source-generated
`System.Text.Json` DTOs (reflection-free/AOT-safe), and a **per-method IPMI fallback** behind the
testable `IIpmiCommandRunner` seam guarded by a read-only `ipmitool` command allowlist. It reuses the
RouterOS three-way TLS policy, an env-backed `IBmcCredentialResolver`, and a `("HPE", null, Redfish,
"1.0.0")` descriptor resolved version-agnostically through `IBmcDriverRegistry`. It also demonstrates
conveying data-source provenance (Redfish vs IPMI) through diagnostics/metrics/logs rather than widening
the shared result records, and the one shared-abstraction change it required — `BmcNetworkInterfaceInfo.Mac`
becoming nullable so a MAC-less NIC can be reported rather than dropped. See
[docs/redfish-discovery.md](redfish-discovery.md) for its data sources, least-privilege iLO permissions,
TLS/credential env vars, and how to run its tests (in-process HTTPS simulator or real iLO), and
[ADR 0009](adr/0009-redfish-ipmi-readonly-bmc-driver.md) for the design decisions.
