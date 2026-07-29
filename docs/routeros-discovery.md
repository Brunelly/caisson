# RouterOS discovery: data sources, permissions and testing

The MikroTik RouterOS driver (`Caisson.Drivers.MikroTik`) discovers Layer-2 topology from RouterOS
switches/routers (including CHR) **read-only**, over the binary RouterOS API on TCP 8728 (plain) or 8729
(TLS). See [ADR 0008](adr/0008-mikrotik-routeros-readonly-driver.md) for the design rationale and
[docs/adding-a-driver.md](adding-a-driver.md) for how a driver plugs into the abstraction.

## Discovery data sources

Each discovery method maps one or more `.../print` commands into a story-3 Switches info record. These are
the **only** commands the driver may send — enforced by `RouterOsReadCommands.Allowlist` and the
`SendCommandAsync` chokepoint, which rejects anything else before any I/O.

| Method | RouterOS command(s) | Produces |
|--------|---------------------|----------|
| `GetDeviceInfoAsync` | `/system/resource/print` (+ `/system/routerboard/print`) | `SwitchDeviceInfo` — version→`OsVersion`, board→`Model`, host→`ManagementIp`, serial (null on CHR) |
| `GetPortsAsync` | `/interface/print` + `/interface/ethernet/print` (physical-port scope) + `/interface/bridge/port/print` (PVID) + `/interface/bridge/vlan/print` (tagged sets) | `SwitchPortInfo` — name, `IsUp`, `Pvid`, `TaggedVlans` |
| `GetLldpNeighborsAsync` | `/ip/neighbor/print` | `LldpNeighbourInfo` — local port → chassis/port/system/mgmt |
| `GetBridgeHostTableAsync` | `/interface/bridge/host/print` | `BridgeHostEntry` — normalized MAC → port |
| `GetVlansAsync` | `/interface/bridge/vlan/print` ∪ `/interface/vlan/print` | `VlanInfo` — VLAN id (+ name), deduped |

`GetDeviceInfoAsync`/`GetPortsAsync`/`GetVlansAsync` treat their secondary commands as **auxiliary**: if
one fails or is unsupported, that section degrades to a per-section diagnostic and the call still succeeds.
`GetPortsAsync` uses `/interface/ethernet/print` to **scope the port list to physical interfaces**: when
that section is available, logical interfaces (bridges, VLAN interfaces, loopbacks) are excluded so they
cannot pollute topology; when it is unavailable the mapper degrades to reporting every interface.

## v6 ↔ v7 field-name differences (best-effort parsing)

The driver targets RouterOS v6 and v7 best-effort. The tolerant `RouterOsRecord` reader absorbs the
variance below via multi-key fallback and never throws on a missing/renamed field:

- **Booleans**: v6 reports `yes`/`no`; v7 reports `true`/`false`. Both (and `1`/`0`) are accepted.
- **Port state**: RouterOS exposes admin state (`disabled`) and link state (`running`) separately; the
  story-3 `SwitchPortInfo.IsUp` collapses them to `running && !disabled`.
- **Neighbours**: chassis identity comes from `mac-address` or `chassis-id`; the remote port from
  `interface-name`/`port-id` (may be absent on v6, leaving `PortId` empty); system name from `identity`.
- **Bridge host**: local port from `on-interface` or `interface`; MACs arrive in any case/separator form
  (`AA:BB:…`, `aabb.ccdd.…`) and are normalized via `MacAddressValue`.
- **VLANs**: `/interface/bridge/vlan` `vlan-ids` uses list/range syntax (`10,20,30-32`, or space-separated
  on some builds), expanded and de-duplicated.
- **Login**: v7 (≥6.43) uses the plaintext `/login name= password=` scheme; older firmware uses the MD5
  challenge-response. The client attempts plaintext first and falls back automatically.

## Least-privilege RouterOS user

Discovery needs **read + api only**. Create a dedicated group and user that explicitly cannot write:

```
/user group add name=caisson-ro policy=read,api,!write,!policy,!sensitive,!ftp,!reboot,!password,!test
/user add name=caisson-ro group=caisson-ro password=<secret>
```

`!write`/`!policy`/`!sensitive` guarantee the account cannot change configuration, edit permissions, or
read secrets even if the driver's allowlist were bypassed — defence in depth on top of the code-level
read-only boundary.

## TLS transport and certificate trust

TLS (port 8729) is now the **default, fail-closed transport** (ADR 0020): `UseTls` defaults to `true` and
is derived explicitly rather than inferred from the port, so a TLS API reachable on a non-standard port is
expressible. The legacy plaintext API (TCP 8728), which sends the API password over the wire in cleartext
immediately after connecting, requires an explicit `AllowPlaintext = true` opt-in — omitting it fails
driver creation outright, and every plaintext connection now logs at `Error` (not `Warning`), since it can
only happen as a conscious operator decision. A rack definition that pairs a configured `TLS_FINGERPRINT`
with a non-TLS switch fails startup (the pin would otherwise be silently unenforced).

CHR ships a self-signed certificate, so a fully trusted chain is unavailable. Rather than blanket-accepting
any certificate (which would leave TLS defenceless against an active man-in-the-middle who could intercept
the cleartext login, CWE-295), the driver enforces one of three outcomes and **never** silently accepts:

1. if a **SHA-256 fingerprint pin** is configured, it is the SOLE authority — the certificate is accepted
   only on an exact match, checked BEFORE anything else, so a certificate that happens to chain to a
   trusted root can never bypass a configured pin (ADR 0019). This is the recommended posture for CHR;
2. otherwise, a fully trusted chain (a proper CA-issued cert) is accepted, with revocation checking enabled;
3. otherwise, an untrusted certificate is accepted only when the operator has **explicitly opted in**, and
   only outside `ASPNETCORE_ENVIRONMENT=Production` — the host refuses to create the driver if this opt-in
   is set under Production (finding #24).

TLS trust is configured via environment variables keyed by the credentials reference (same slug rule as
credentials); the fingerprint pin falls back to a global default, but the untrusted-certificate opt-in does
**not** — it is per-slug only, so the blast radius of ever setting it is exactly one device:

```
# Pin the self-signed CHR certificate (openssl x509 -fingerprint -sha256; separators/case ignored):
export CAISSON_SWITCH_{REF}_TLS_FINGERPRINT=AB:CD:...:EF   # or CAISSON_SWITCH_TLS_FINGERPRINT

# Or, only where you accept the MITM risk (refused outright under Production):
export CAISSON_SWITCH_{REF}_TLS_ALLOW_UNTRUSTED=true        # per-slug only — no global fallback
```

With neither set, TLS to a self-signed CHR is **rejected** — configure a fingerprint pin to use it securely.

Wire caps guard against a compromised or misbehaving device amplifying wire bytes into heap: at most 4096
words and 16 MiB of aggregate word bytes per sentence (`RouterOsSentence`), and at most 100,000 `!re` rows
per reply (`RouterOsApiClient.MaxRowsPerReply`) — on top of the existing 8 MiB per-word cap. A query's
sub-commands (e.g. `GetPortsAsync`'s four RouterOS calls) now share ONE overall per-device-timeout budget
via a linked `CancellationTokenSource`, rather than each getting its own full timeout window.

## Credentials

`SwitchConnectionOptions.CredentialsRef` is an opaque reference, never the secret, and must match
`^[A-Za-z0-9_]{1,64}$` (validated at driver-creation time and again, for the whole configuration, by
`RackDefinitionValidation` at startup) — an empty reference is rejected outright rather than silently
falling back to the global credential, and the strict charset makes the env-var slug derivation injective
(two references that used to normalize to the same slug via a stripped separator, e.g. `rack1-sw` and
`rack1.sw`, are no longer both valid). The default `EnvSwitchCredentialResolver` reads
`CAISSON_SWITCH_{REF}_USERNAME`/`_PASSWORD` (the reference upper-cased), falling back to the global
`CAISSON_SWITCH_USERNAME`/`_PASSWORD`. In CI these come from GitHub Actions secrets. Secrets are never
written to logs or `DriverError.Message`. Every credential-bearing settings/record type overrides
`ToString()` to omit the password, so an accidental `.ToString()` can never leak it.

## Running the tests

Unit tests (no network):

```
dotnet test tests/Caisson.Drivers.MikroTik.Tests
```

Integration tests run against the in-process `RouterOsApiSimulator` by default — no hardware, no container:

```
dotnet test tests/Caisson.Drivers.MikroTik.IntegrationTests
```

To run the same suite against a **real CHR** instead of the simulator, point it at the device and supply
credentials, then run the smoke tests (profile-specific simulator tests self-skip against real hardware):

```
export CAISSON_CHR_HOST=192.0.2.10          # or host:port; defaults to 8728
export CAISSON_SWITCH_USERNAME=caisson-ro
export CAISSON_SWITCH_PASSWORD=<secret>
dotnet test tests/Caisson.Drivers.MikroTik.IntegrationTests
```

The simulator replays committed fixtures under `tests/Caisson.Drivers.MikroTik.IntegrationTests/Fixtures/`
(`v7`, `v6`, `empty-lldp`, `failure`), so cross-firmware parsing and partial-failure handling are covered
deterministically. Per-command logs are emitted to the test output for debugging.
