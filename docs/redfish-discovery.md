# Redfish / IPMI discovery: data sources, permissions and testing

The Redfish BMC driver (`Caisson.Drivers.Redfish`) discovers server identity and NIC MAC addresses from HP
iLO / Redfish baseboard management controllers **read-only**, over HTTPS/JSON, with a read-only **IPMI
fallback** for firmware where Redfish is unavailable or insufficient. See
[ADR 0009](adr/0009-redfish-ipmi-readonly-bmc-driver.md) for the design rationale and
[docs/adding-a-driver.md](adding-a-driver.md) for how a driver plugs into the abstraction.

## Discovery data sources

Each discovery method navigates one or more Redfish resources (or, on fallback, runs one or more read-only
`ipmitool` subcommands) and maps the result into a story-3 Bmc info record. These are the **only** requests
the driver may make — enforced by `RedfishReadPaths.IsReadOnlyGet` / `IpmiReadCommands.IsReadOnly` and the
transport chokepoints, which reject anything else before any I/O.

| Method | Redfish path(s) | IPMI fallback | Produces |
|--------|-----------------|---------------|----------|
| `GetSystemInventoryAsync` | `/redfish/v1` → `/redfish/v1/Systems` → `.../Systems/{id}` | `mc info`, `fru print` | `BmcSystemInventory` — UUID, SerialNumber, Model, HostName |
| `GetNetworkInterfacesAsync` | `.../Systems/{id}/EthernetInterfaces` → each member | `lan print` (BMC LAN MAC) | `BmcNetworkInterfaceInfo` — normalized MAC (or `null` + diagnostic), link state |
| `GetBiosInfoAsync` | `.../Systems/{id}` (`BiosVersion`, `Manufacturer`) | `fru print` (vendor only) | `BmcBiosInfo` — vendor, version |

Navigation only follows `@odata.id` links that themselves re-pass the read-only allowlist. Every
`/Actions/` endpoint (reset, power, virtual media) and every `/Settings` write resource is hard-rejected, so
the driver can never invoke a mutating operation.

## Server identity and MAC handling

- **Server identity** resolves in the order **UUID → SerialNumber → composite** (`Redfish Id` + endpoint
  host). When neither UUID nor SerialNumber is present, the composite is used and a `Warning` diagnostic
  records that identity is degraded (story #5 answered question).
- **MAC addresses** are normalized through the domain `MacAddressValue` (accepts `00-1a-…`, `001A.2B3C.…`,
  colon and bare forms). A NIC whose MAC is **missing or unparseable is still included** with `Mac=null` and
  a per-NIC diagnostic naming the interface, so the gap stays visible for correlation debugging. Storage is
  the domain-canonical value; the uppercase-colon presentation form is an API-edge concern (ADR 0004).

## IPMI fallback trigger conditions

The fallback is **per-method**. The driver attempts Redfish first and falls back to the equivalent read-only
IPMI command(s) when Redfish is:

- **unreachable / times out / rejects the credentials** (401/403), or
- **structurally insufficient** — e.g. an empty NIC list, or a NIC list with no usable MAC.

On fallback it appends a `Warning` diagnostic for the Redfish failure reason and a `FallbackSource`
diagnostic naming the section, tags the metric `source=ipmi`, and emits a secret-free fallback-trace log
line. If neither Redfish nor IPMI can read a section, the call returns a structured `DriverError` (it never
throws for expected failures).

## Least-privilege iLO account

Discovery needs **read-only** access. Create a dedicated iLO user with only the read privileges (no
configure / virtual-media / power / user-administration rights):

```
# iLO 5 example (via the iLO web UI or RESTful interface tool):
#   User: caisson-ro
#   Privileges: Login only  (NOT Configure iLO Settings, Virtual Power and Reset,
#               Virtual Media, Host BIOS, User Administration)
```

The same credential is used for the IPMI lanplus fallback; grant it the IPMI **User** privilege level
(read-only), never Operator/Administrator. This is defence in depth on top of the code-level read-only
allowlists.

## TLS transport and certificate trust

All Redfish traffic is HTTPS. iLO ships a self-signed certificate, so a fully trusted chain is usually
unavailable. Rather than blanket-accepting any certificate (which would leave TLS defenceless against an
active man-in-the-middle, CWE-295), the driver enforces one of three outcomes and **never** silently
accepts:

1. a fully trusted chain (a proper CA-issued cert) is accepted;
2. otherwise, if a **SHA-256 fingerprint pin** is configured, the certificate is accepted only on an exact
   match — the recommended posture for iLO;
3. otherwise, an untrusted certificate is accepted only when the operator has **explicitly opted in**.

TLS trust is configured via environment variables keyed by the credentials reference (same slug rule as
credentials), falling back to a global default:

```
# Pin the self-signed iLO certificate (openssl x509 -fingerprint -sha256; separators/case ignored):
export CAISSON_BMC_{REF}_TLS_FINGERPRINT=AB:CD:...:EF   # or CAISSON_BMC_TLS_FINGERPRINT

# Or, only where you accept the MITM risk (integration tests only, disallowed in production):
export CAISSON_BMC_{REF}_TLS_ALLOW_UNTRUSTED=true       # or CAISSON_BMC_TLS_ALLOW_UNTRUSTED
```

With neither set, TLS to a self-signed iLO is **rejected** — configure a fingerprint pin to use it securely.

## Credentials

`BmcConnectionOptions.CredentialsRef` is an opaque reference, never the secret. The default
`EnvBmcCredentialResolver` reads `CAISSON_BMC_{REF}_USERNAME`/`_PASSWORD` (the reference upper-cased,
non-alphanumerics → `_`), falling back to the global `CAISSON_BMC_USERNAME`/`_PASSWORD`. In CI these come
from GitHub Actions secrets. One credential serves both Redfish Basic auth and IPMI lanplus. Secrets are
never written to logs or `DriverError.Message`; the IPMI password is passed to `ipmitool` via the
`IPMI_PASSWORD` environment variable (`-E`), never on the argument vector.

## Running the tests

Unit tests (no network):

```
dotnet test tests/Caisson.Drivers.Redfish.Tests
```

Integration tests run against the in-process HTTPS `RedfishSimulator` and a stubbed `ipmitool` runner by
default — no hardware, no container:

```
dotnet test tests/Caisson.Drivers.Redfish.IntegrationTests
```

The simulator performs a real TLS handshake with a generated self-signed certificate (exercising the
driver's certificate validation) and replays committed fixtures under
`tests/Caisson.Drivers.Redfish.IntegrationTests/Fixtures/` (`ilo-success`, `ilo-missing-serial`,
`ilo-empty-nics`, `ilo-nic-missing-mac`, `ilo-auth-fail`, plus `ipmi-*.txt`), so success, partial/missing
fields, auth failure and Redfish→IPMI fallback are covered deterministically. Per-request logs are emitted
to the test output for debugging.

To run against **real hardware** instead of the simulator, point the suite at the device and supply
credentials (profile-specific simulator tests self-skip against real hardware):

```
export CAISSON_ILO_HOST=192.0.2.20         # or host:port; defaults to 443
export CAISSON_IPMI_HOST=192.0.2.20        # optional: enables the real-ipmitool opt-in
export CAISSON_BMC_USERNAME=caisson-ro
export CAISSON_BMC_PASSWORD=<secret>
export CAISSON_BMC_TLS_FINGERPRINT=AB:CD:...:EF   # pin the real iLO certificate
dotnet test tests/Caisson.Drivers.Redfish.IntegrationTests
```
