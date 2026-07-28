# 0009 — Redfish / IPMI read-only BMC discovery driver

## Status
Accepted

## Context
Story #5 delivers the first concrete BMC driver: a strictly read-only HP iLO / Redfish discovery driver
with an IPMI fallback, on top of the story-3 abstractions (ADR 0006) and mirroring the story-4 MikroTik
driver's quality and security posture (ADR 0008). It must read server identity (model, serial, UUID,
hostname) and NIC MAC addresses over Redfish (HTTPS/JSON); fall back to read-only IPMI on older firmware
where Redfish is unavailable or insufficient; never power, reset or configure a server; log no secrets; and
run in CI with no physical hardware. The story's answered questions fix: the `ServerId` fallback (UUID →
SerialNumber → Redfish `Id` + endpoint host composite, recorded as a degraded-identity warning); MAC-less
NICs are **included** with `MacAddress=null` plus a per-NIC diagnostic; and simulator TLS uses a test-only
certificate validation override scoped to integration tests and disallowed in production.

## Decision
- **Transport: BCL `HttpClient`/`SocketsHttpHandler` + source-generated `System.Text.Json`, zero third-party
  deps.** No Redfish/IPMI/HTTP NuGet — the client is an internal, BCL-only implementation and the DTOs are
  (de)serialized through a `[JsonSerializable]` `JsonSerializerContext` (reflection-free, NativeAOT-safe per
  the story's constraint). This keeps licensing clean and the driver AOT-compatible, mirroring story-4's
  dependency-light stance (ADR 0001/0008). The new project is `Caisson.Drivers.Redfish` (protocol-named; the
  IPMI fallback lives here too, as an internal detail).
- **Double-enforced read-only boundary (NFR1/AC1/AC2).** The interface-level guarantee (the `ReadOnly`
  namespace, ADR 0006) is backed by two transport-level allowlists, both checked **before any I/O**:
  `RedfishReadPaths.IsReadOnlyGet` requires a `GET`, a path under `/redfish/v1`, no `/Actions/` segment (a
  hard reject of every Redfish mutating action — reset/power/virtual-media) and no `/Settings` segment, and
  membership in a `FrozenSet` of allowed resource prefixes (service root, Systems, Managers, Chassis);
  `IpmiReadCommands.IsReadOnly` allowlists only read subcommands (`mc info`, `fru print`, `lan print`,
  `sdr elist`/`sdr type`, `chassis status`) and hard-rejects any write token (`power`, `reset`, `raw`,
  `sol`, `user`, `sel clear`, `sensor set`, boot verbs). These are the code-reviewable NFR1 artifacts.
- **TLS is validated, never blanket-accepted (CWE-295).** The three-way policy is reused verbatim from
  RouterOS: a trusted chain, a configured SHA-256 fingerprint pin (the recommended posture for the
  self-signed iLO certificate), or an explicit per-connection opt-in — otherwise rejected. TLS trust (pin /
  opt-in) is sourced from environment variables keyed by the credentials reference, so `BmcConnectionOptions`
  is not widened.
- **HTTP Basic auth, not Redfish session tokens.** Each GET carries an HTTP Basic header. A Redfish session
  login is a `POST` to `/SessionService/Sessions`, which would itself be a mutating call and blur the
  read-only boundary; Basic keeps every request a pure read.
- **Redfish-first, per-method IPMI fallback; provenance via diagnostics/metrics/logs, not a widened contract.**
  Each method attempts Redfish first and, on an unreachable/timeout/auth failure **or** structurally
  insufficient data (an empty or entirely MAC-less NIC list), falls back to the equivalent read-only IPMI
  command(s), appending a `Warning` diagnostic for the Redfish failure reason and a `FallbackSource`
  diagnostic naming the section, tagging the metric `source=ipmi`, and emitting a secret-free fallback-trace
  log line. Following ADR 0008's precedent, `BmcSystemInventory`/`BmcBiosInfo` are **not** widened to carry a
  per-field `DataSource`; provenance lives in diagnostics + metrics + logs. One small additive
  `ReasonCode.FallbackSource` member was added to the domain enum for this.
- **`BmcNetworkInterfaceInfo.Mac` widened to `MacAddressValue?` (tech-lead flag).** The story's answered
  question requires a MAC-less NIC to be **included** with `Mac=null` and a per-NIC diagnostic "to preserve
  visibility for correlation debugging". This is a shared-abstraction change; its blast radius is minimal (a
  nullable value type; only the abstraction test mocks reference the type) and the whole solution still
  compiles. The domain-canonical `MacAddressValue` is stored as-is — AC1's uppercase-colon rendering is a
  presentation concern handled at the API edge (ADR 0004 storage rule), not in the driver.
- **Overall budget via a single linked CTS.** One `CancellationTokenSource.CancelAfter(options.Timeout)` per
  call is the overall budget shared across the multi-GET Redfish navigation and any IPMI fallback within that
  call (treating `Timeout` as the P95≤10s budget, as RouterOS does). Only caller cancellation surfaces as
  `OperationCanceledException`; every expected failure maps to a `DriverError` (401/403 →
  `AuthenticationFailed` non-retryable; timeout/budget → `ConnectionTimeout` retryable; unreachable →
  `DeviceUnreachable`/`ConnectionRefused`; TLS → `ProtocolError`; 404/schema/JSON → `ParseError`). Messages
  are secret-free and surface machine codes (`REDFISH_TIMEOUT`, `REDFISH_SCHEMA_MISSING_FIELD`,
  `IPMI_AUTH_FAILED`).
- **`ipmitool` as an external process.** `ProcessIpmiCommandRunner` shells out to `ipmitool -I lanplus`,
  passing the password via the `IPMI_PASSWORD` env var and `-E` (never argv or logs); an absent binary is a
  clean unavailable result, not a crash. This external-process dependency is a constraint the future
  NativeAOT appliance-agent story must satisfy (the Redfish path is fully self-contained; the IPMI fallback
  requires `ipmitool` on the host).
- **Descriptor `("HPE", null, Redfish, "1.0.0")`, version-agnostic registry (ADR 0007).** Vendor `HPE`, a
  generic iLO line (`Model=null`), connection kind `Redfish` (IPMI is an internal fallback, not a separately
  resolvable kind). Resolution is by vendor/model/connection-kind, proven end-to-end in the integration tests
  — the first exercise of ADR 0007 for a BMC driver. `BmcConnectionOptions.Port` defaults to 443; IPMI is
  fixed to lanplus port 623 (neither widens the shared options type).
- **Credential resolution via `IBmcCredentialResolver`.** The default `EnvBmcCredentialResolver` maps a
  `CredentialsRef` to `CAISSON_BMC_{SLUG}_USERNAME`/`_PASSWORD` env vars (CI secrets); one resolved
  credential serves both Redfish Basic auth and IPMI lanplus (realistic for iLO). Secrets are resolved lazily
  per connection and never logged.
- **Simulator-first CI with a real-hardware opt-in.** An in-process HTTPS `RedfishSimulator` (a loopback
  `TcpListener` + real server-side `SslStream` handshake with a generated self-signed cert, ASP.NET-free)
  replays committed iLO JSON fixtures and exercises the driver's `ValidateServerCertificate` end-to-end;
  a stubbed `IIpmiCommandRunner` replays committed `ipmitool` text. `RedfishBmcFixture` prefers real hardware
  when `CAISSON_ILO_HOST`/`CAISSON_IPMI_HOST` is set and falls back to the simulator otherwise. CI pins the
  simulator cert by SHA-256 fingerprint (production-safe); one test exercises the allow-untrusted override,
  scoped to integration tests only.

## Consequences
- The driver has no third-party dependencies beyond the interfaces-only DI/logging abstractions, staying
  AOT-friendly and licensing-clean; Redfish parsing is fully reflection-free.
- Read-only safety is enforced twice per protocol (namespace + Redfish path allowlist + IPMI command
  allowlist) and covered by unit tests, including a reflection guard that no driver method mutates.
- Discovery is resilient: expected failures map to `DriverError` codes, partial/missing data degrades to
  diagnostics, and Redfish gaps fall back to IPMI with data-source provenance — without failing the whole run.
- `BmcNetworkInterfaceInfo.Mac` is now nullable across the abstraction; downstream consumers must treat a
  null MAC as "observed interface, MAC unavailable" (a diagnostic explains why).
- The IPMI fallback depends on `ipmitool` being present on the host; the NativeAOT appliance-agent story must
  account for this external-process requirement. TLS certificate pinning is implemented; a real HTTPS
  simulator makes the CWE-295 policy testable without hardware.
