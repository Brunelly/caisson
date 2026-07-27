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
