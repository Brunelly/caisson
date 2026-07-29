# 0020: Switch transport defaults to TLS; plaintext requires an explicit opt-in

## Status

Accepted

## Context

Security review `security-review-5` (finding #8) found that `RouterOsSwitchDriverFactory.DefaultPort` was
8728 (the plaintext RouterOS API), so a `Discovery:Racks` entry that omitted `Port` — the natural minimal
configuration — sent the API password as a cleartext protocol word immediately after connect. TLS could
not be requested explicitly either: it was inferred solely from `Port == 8729`, so a TLS API reachable on
a non-standard port (NAT, port-forward, hardened deployment) silently got a plaintext session, and any
configured `TLS_FINGERPRINT` was silently ignored on such a connection.

`Caisson.Drivers.Simulators.RouterOsApiSimulator` already supports a genuine server-side TLS handshake
(an optional `X509Certificate2` constructor parameter — see `TlsHandshakeIntegrationTests`), and every
simulator-backed test binds an ephemeral port and passes it explicitly, so flipping the *default* transport
does not depend on, or risk breaking, the simulation-first CI harness.

## Decision

- `SwitchConnectionOptions` and `DeviceDefinitionEntry` gain explicit `UseTls` (default `true`) and
  `AllowPlaintext` (default `false`) fields. TLS is derived from `UseTls`, never inferred from the port.
- `RouterOsSwitchDriverFactory.DefaultPort` is now `TlsPort` (8729); the legacy plaintext port 8728 is
  reachable only when both an explicit port (or `PlaintextPort`) AND `AllowPlaintext = true` are set —
  the same fail-closed shape already used for `AllowUntrustedCertificate`. Omitting `AllowPlaintext` on a
  non-TLS connection throws at driver-creation time.
- The plaintext-connection log line is escalated from `Warning` to `Error`, since every occurrence is now
  a conscious operator opt-in rather than routine noise.
- A rack definition that pairs a configured `TLS_FINGERPRINT` with `UseTls = false` fails startup (the pin
  would otherwise be silently unenforced) — checked once for the whole configuration in
  `RackDefinitionValidation`, and defensively again per-connection in the factory.

## Consequences

- Every existing plaintext-simulator test (`Caisson.Drivers.MikroTik.IntegrationTests`,
  `Caisson.VirtualRack.*`) now passes `UseTls: false, AllowPlaintext: true` explicitly — this is
  deliberate: the test intent (exercise the plaintext transport) is now visible in the test itself
  instead of being an artifact of an omitted port.
- A minimal `Discovery:Racks` entry (host + credentials only, no port) now defaults to the TLS transport,
  closing the "natural minimal configuration is cleartext" gap the finding described.
- This is a behaviour change for any existing production deployment that relies on the omitted-port
  default being plaintext 8728: such a deployment must now set `UseTls: false` and `AllowPlaintext: true`
  explicitly, or (preferred) enable TLS on the switch's RouterOS API and rely on the new default.
