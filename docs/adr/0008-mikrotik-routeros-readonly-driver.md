# 0008 — MikroTik RouterOS read-only discovery driver

## Status
Accepted

## Context
Story #4 delivers the first concrete switch driver: a strictly read-only MikroTik RouterOS (CHR)
discovery driver on top of the story-3 abstractions (ADR 0006). It must collect ports, LLDP neighbours,
bridge/MAC host tables and VLAN assignments; tolerate RouterOS v6↔v7 firmware variance; never write to a
device; capture enough evidence for audit; and run in CI with no physical hardware. The story's answered
questions fix the transport (RouterOS API on 8728/8729, SSH deferred) and the firmware target (v6 and v7
best-effort). ADR 0006 also deferred credential resolution behind `CredentialsRef` without changing
`SwitchConnectionOptions`.

## Decision
- **Transport: the binary RouterOS API over TCP 8728 (plain) / 8729 (TLS).** The REST API is v7-only and
  would drop v6 support; SSH screen-scraping is deferred. The client is an internal, BCL-only
  implementation (`RouterOsApiClient` over `TcpClient`/`SslStream`) — **no third-party MikroTik NuGet** —
  to avoid heavy/unsafe dependencies, keep licensing clean and stay AOT-compatible (ADR 0001/0006). It
  implements the length-prefixed word framing, sentence read/write, `!re`/`!done`/`!trap`/`!fatal`
  handling, and both the post-6.43 plaintext login and the pre-6.43 MD5 challenge-response fallback.
- **Double-enforced read-only boundary (NFR1/AC1).** The interface-level guarantee (the `ReadOnly`
  namespace, ADR 0006) is backed by a transport-level command allowlist: `RouterOsReadCommands.Allowlist`
  is a `FrozenSet` of exactly the nine `.../print` paths, and the single `SendCommandAsync` chokepoint
  throws `InvalidOperationException` for anything else **before any socket I/O**. This is the
  code-reviewable artifact NFR1 asks for.
- **Evidence/audit (AC4) and metrics (NFR6) are folded into structured logging + `System.Diagnostics`,
  not a widened shared contract.** The `SendCommandAsync` chokepoint emits one structured, secret-free
  log line per command (Command, Host, ElapsedMs, Outcome) and the driver wraps each call in an
  `ILogger` scope carrying a correlation id (`Activity.Current`) and switch host; `RouterOsMetrics` uses
  `System.Diagnostics.Metrics.Meter` (BCL, OTel-scrapeable later, no OpenTelemetry SDK dependency) with
  `driver=routeros`, `query=…`, `outcome=success|fail` tags. The story-3 Switches info records
  (`SwitchDeviceInfo`, `SwitchPortInfo`, `LldpNeighbourInfo`, `BridgeHostEntry`, `VlanInfo`) are **not**
  widened, because the task binds this work to "map into the story-3 info records" and only authorizes the
  registry as an abstraction change; those records are also depended on by story #5.
  **Tech-lead flag:** if richer per-record raw evidence must live on the result contract (e.g.
  `AdminUp`/`LinkUp` split, a raw key/value slot, source timestamps), that should be a separate, scoped
  follow-up PR against `Caisson.Drivers.Abstractions`, not an uninstructed widening here. Today RouterOS's
  separate admin (`disabled`) and link (`running`) states are collapsed into the single `SwitchPortInfo.IsUp`.
- **Credential resolution via `ISwitchCredentialResolver`.** The default `EnvSwitchCredentialResolver`
  maps a `CredentialsRef` to environment variables (CI secrets), resolving the ADR 0006 deferral
  **without** changing `SwitchConnectionOptions`. Secrets are resolved lazily per connection and never
  logged.
- **Simulator-first CI with a real-CHR opt-in.** An in-process `RouterOsApiSimulator` speaks the same wire
  protocol (with an independent framing implementation) and replays committed v6/v7/empty-lldp/failure
  fixtures; `RouterOsChrFixture` prefers a real CHR when `CAISSON_CHR_HOST`/`CAISSON_CHR_API` is set and
  falls back to the simulator otherwise — mirroring `PostgresFixture`'s `CAISSON_TEST_DB` pattern. CHR is a
  licensed KVM image unfit for deterministic hardware-free runners, so the simulator is the CI path.
- **Version-agnostic registry (ADR 0007).** The driver registers a `("MikroTik", null, RouterOsApi,
  "1.0.0")` descriptor and is resolved by vendor/model/connection-kind, proven end-to-end in the
  integration tests.

## Consequences
- The driver has no third-party dependencies beyond the interfaces-only DI/logging abstractions, staying
  AOT-friendly and licensing-clean.
- Read-only safety is enforced twice (namespace + transport allowlist) and covered by unit tests.
- Discovery is resilient: expected failures map to `DriverError` codes (auth non-retryable, timeout
  retryable) and a single failed section/row degrades to a diagnostic rather than failing the whole run.
- AC4's on-contract "raw source records" requirement is intentionally satisfied via logs/metrics for now;
  the ADR flags the follow-up should the result contract need to carry evidence directly.
- SSH transport, certificate pinning for 8729, and richer per-record evidence are explicit future work.
