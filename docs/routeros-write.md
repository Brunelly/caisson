# RouterOS write driver: safe apply, confirmed-commit, and testing

The MikroTik RouterOS write driver (`Caisson.Drivers.MikroTik.RouterOsSwitchMutatingDriver`) sets a
single switch port's access VLAN (PVID) over the binary RouterOS API, wrapped in a confirmed-commit
safety mechanism so an unconfirmed or unverifiable change self-reverts on the device. See
[ADR 0031](adr/0031-routeros-safe-apply-and-confirmed-commit.md) for the design rationale and
[docs/routeros-discovery.md](routeros-discovery.md) for the read-only driver this shares its transport
hardening with.

## Scope

The driver exposes exactly one operation, `ISwitchMutatingDriver.SetAccessVlanAsync`: set a port's
access VLAN, identified by its stable interface name (e.g. `ether1`). It mutates only the port's PVID.
Bridge-VLAN tagged/untagged membership is read (to assert access-VLAN semantics and to verify the
change) but never written; moving a port between tagged VLAN sets, trunk configuration, ACLs, firmware
upgrades and any other write operation are all out of scope for this story. The target VLAN must already
be configured on the switch's bridge VLAN table — the driver never auto-creates one.

## The write allowlist

`RouterOsWriteCommands.Allowlist` is a SEPARATE, disjoint set from the read-only
`RouterOsReadCommands.Allowlist` (which is never widened by this driver):

| Command | Purpose |
|---------|---------|
| `/interface/bridge/port/print` | Read a port's current PVID (before-state and post-apply verification) |
| `/interface/bridge/port/set` | Set a port's PVID — the one mutating command this driver ever sends |
| `/interface/bridge/vlan/print` | Confirm the desired VLAN is configured; read untagged-membership evidence |
| `/system/scheduler/print` | (Reserved for diagnostics — the driver does not currently issue this itself in the happy path) |
| `/system/scheduler/add` | Arm the confirmed-commit self-revert job before applying |
| `/system/scheduler/remove` | Cancel the armed job once the change is verified — the confirm signal |

`RouterOsWriteApiClient.ExecuteAsync` rejects anything off this list before any socket I/O, mirroring
the read client's chokepoint.

## The safe-apply / confirmed-commit flow

1. **Validate** the requested VLAN id is in the valid 802.1Q range (1–4094) — before any device I/O.
   Outside that range: `InvalidVlanId`, zero writes.
2. **Read before-state**: the port's PVID (`/interface/bridge/port/print?interface=<port>`) and the
   bridge VLAN table (`/interface/bridge/vlan/print`), which together give the minimal untagged-membership
   subset needed to assert access-VLAN semantics. No matching port: `PortNotFound`. More than one
   matching row: `AmbiguousPort` (fail fast rather than guess). The desired VLAN absent from the bridge
   VLAN table: `VlanNotConfigured` (the driver never auto-creates a VLAN).
3. **Idempotency**: if the port's current PVID already equals the desired VLAN, the call is a no-op —
   `NoOpAlreadyDesiredState` — with zero further device writes (AC2/NFR3).
4. **Dry-run**: if `DryRun` is set, the driver returns the intended `SwitchChangePlan` and a before/after
   preview without changing device state — `DryRunPlanned`.
5. **Arm the rollback**: a one-shot `/system/scheduler/add` job is armed BEFORE anything is applied. Its
   `on-event` is a fixed, driver-owned script template (`/interface/bridge/port/set [find interface="<port>"]
   pvid=<before-pvid>`) with only the already-validated port name and the already-observed before-PVID
   substituted — never raw caller input — and its `start-time` is `+<confirmWindowSeconds>s` from now.
6. **Apply**: `/interface/bridge/port/set =.id=<id> =pvid=<desired>`.
7. **Verify**: re-read the port's PVID. A mismatch means the change is NOT confirmed —
   `VerificationFailed` — and the armed job self-reverts once its window elapses.
8. **Confirm**: on a successful verification, `/system/scheduler/remove` cancels the armed job — this IS
   the confirm signal — and the outcome becomes `Applied`, `Confirmed=true`.

Because `SetAccessVlanAsync` always performs steps 5–8 within a single call, the confirm window's 30
second (default) duration only ever matters for the FAILURE path — a crash between apply and confirm, or
a verification mismatch — so it does not conflict with the sub-5-second P95 target for the happy path
(NFR4). An internal `BeginChangeAsync`/`ConfirmChangeAsync` seam lets tests exercise the real rollback
path by calling `BeginChangeAsync` and deliberately never confirming.

## Why a scheduler job, not Safe Mode

RouterOS's interactive "Safe Mode" is scoped to the API session: it reverts on a dropped connection, but
does nothing to protect against a control-plane crash that leaves the connection technically open, and a
caller can "confirm" it away simply by disconnecting cleanly. A `/system/scheduler` one-shot job is
window-based and independent of any single connection's lifetime — it fires even if the entire control
plane is gone — which is the stronger safety property the story's "can't brick the un-bricker" constraint
calls for.

## Least-privilege RouterOS user

The write path needs a MORE privileged RouterOS user than the read-only discovery user documented in
[docs/routeros-discovery.md](routeros-discovery.md) (whose policy explicitly denies `!write`). A
dedicated write user should be scoped as narrowly as the RouterOS permission model allows for bridge/VLAN
configuration and scheduler management, and must be provisioned and referenced via its own
`CredentialsRef` — do not reuse the read-only credential for write operations.

## TLS transport and certificate trust

Identical to the read driver (ADR 0019/0020): `UseTls` defaults to `true`, `AllowPlaintext` defaults to
`false` and must be explicitly opted into, and a configured SHA-256 fingerprint pin is the sole
certificate-trust authority when present. This is enforced by the SAME shared connection code
(`RouterOsApiConnection`) the read client uses — see ADR 0031 — so there is exactly one place this policy
can drift.

## Confirmed-commit window configuration

The default confirm window is 30 seconds (the story's answered question), applied when neither
`SwitchMutatingConnectionOptions.ConfirmWindow` nor an individual `SetAccessVlanRequest.ConfirmWindow` is
set. Configure a longer window for high-latency/high-jitter links via either the connection-level default
or per-request.

## Audit

Every outcome — dry-run, no-op, applied, verification-failed, rolled-back, or rejected — produces a
`SwitchChangeAuditRecord` (before/after config subset, reason code, correlation id, dry-run flag, confirm
window, verification result, a `TimeProvider`-sourced timestamp, and actor identity) on
`SetAccessVlanOutcome.Audit`. The driver assembly has no EF Core reference and never persists this
itself — persisting it via `TopologyAuditEvent`/`IAuditEventWriter` is the future apply-API's
responsibility (story #65).

## Running the tests

Unit tests (no network), against a fake write client:

```
dotnet test tests/Caisson.Drivers.MikroTik.Tests
```

Integration tests run against the in-process, stateful `RouterOsApiSimulator` by default — no hardware,
no container, no real 30-second waits (the simulator exposes a deterministic `AdvanceTime`/
`FireDueRollbacks` hook so CI never sleeps):

```
dotnet test tests/Caisson.Drivers.MikroTik.IntegrationTests
```

The same `CAISSON_CHR_HOST` opt-in documented in `docs/routeros-discovery.md` applies to the write suite.
