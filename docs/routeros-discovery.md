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

The RouterOS plaintext API (TCP 8728) sends the API password over the wire in cleartext immediately after
connecting, so it is protected only against a passive eavesdropper. **Prefer the TLS transport (port 8729)
in any environment where an on-path attacker is plausible.** The driver logs a secret-free warning whenever
a password will be sent over a non-TLS connection, so a plaintext choice is always a conscious one.

CHR ships a self-signed certificate, so a fully trusted chain is unavailable. Rather than blanket-accepting
any certificate (which would leave TLS defenceless against an active man-in-the-middle who could intercept
the cleartext login, CWE-295), the driver enforces one of three outcomes and **never** silently accepts:

1. a fully trusted chain (a proper CA-issued cert) is accepted;
2. otherwise, if a **SHA-256 fingerprint pin** is configured, the certificate is accepted only on an exact
   match — the recommended posture for CHR;
3. otherwise, an untrusted certificate is accepted only when the operator has **explicitly opted in**.

TLS trust is configured via environment variables keyed by the credentials reference (same slug rule as
credentials), falling back to a global default:

```
# Pin the self-signed CHR certificate (openssl x509 -fingerprint -sha256; separators/case ignored):
export CAISSON_SWITCH_{REF}_TLS_FINGERPRINT=AB:CD:...:EF   # or CAISSON_SWITCH_TLS_FINGERPRINT

# Or, only where you accept the MITM risk, opt in to accepting an untrusted certificate:
export CAISSON_SWITCH_{REF}_TLS_ALLOW_UNTRUSTED=true       # or CAISSON_SWITCH_TLS_ALLOW_UNTRUSTED
```

With neither set, TLS to a self-signed CHR is **rejected** — configure a fingerprint pin to use it securely.

## Credentials

`SwitchConnectionOptions.CredentialsRef` is an opaque reference, never the secret. The default
`EnvSwitchCredentialResolver` reads `CAISSON_SWITCH_{REF}_USERNAME`/`_PASSWORD` (the reference upper-cased,
non-alphanumerics → `_`), falling back to the global `CAISSON_SWITCH_USERNAME`/`_PASSWORD`. In CI these come
from GitHub Actions secrets. Secrets are never written to logs or `DriverError.Message`.

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
