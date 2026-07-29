# 0031 — RouterOS safe apply and confirmed-commit auto-rollback

## Status

Accepted

## Context

Story #66 delivers Caisson's first WRITE path: setting a switch port's access VLAN over the RouterOS
API. Every prior driver story (#4/#8, ADR 0006/0008) was deliberately read-only, with the safety
boundary enforced by the `ReadOnly` namespace, a reflection guard (`SafetyBoundaryGuardTests`), and a
print-only command allowlist (`RouterOsReadCommands`). Introducing a write capability must not weaken
any of that, and must add its own safety net: a change that cannot be verified, or is simply never
confirmed (a crashed caller, a lost connection, an operator walking away), must never leave the switch
in a state that severs its own management path or drifts silently from the plan ("can't brick the
un-bricker").

ADR 0006 (naming `ISwitchDiscoveryDriver` rather than a plain `ISwitchDriver`) and
`docs/adding-a-driver.md` §5 both explicitly reserved a future `*Mutating` interface pair rather than
widening the read-only one. This story is scoped to exactly one operation (set access VLAN on one port)
per the story's in-scope/out-of-scope lists and NFR1.

## Decision

**Namespace and interface.** `ISwitchMutatingDriver` lives in a new `Caisson.Drivers.Abstractions.Mutating`
namespace (not `ReadOnly`), honouring ADR 0006's reservation. It exposes exactly one method,
`SetAccessVlanAsync`, verified by a new `WriteSafetyBoundaryGuardTests` (the existing
`SafetyBoundaryGuardTests` is unmodified and still passes, since nothing here lives in `ReadOnly`).

**Result vocabulary.** The write path reuses `DriverResult<T>`/`DriverError`/`DriverErrorCode`/
`DriverDiagnostic` as-is rather than inventing a parallel result type. Only infrastructure failures
(connect/auth/timeout/protocol) return `DriverResult.Fail`; every domain outcome — dry-run planned,
no-op, rejected VLAN, verification failure, applied, or rolled back — rides on `DriverResult.Ok`
carrying a `SetAccessVlanOutcome` with a NEW `SwitchChangeReasonCode` enum and a `SwitchChangeAuditRecord`.
`SwitchChangeReasonCode` is deliberately separate from `Caisson.Domain.Enums.ReasonCode`, whose doc
comment scopes it to per-item correlation ambiguity recorded during discovery, not write outcomes.

**Bounded surface, no raw commands.** `SetAccessVlanRequest`/`SetAccessVlanOutcome`/`SwitchChangePlan`
accept and return only typed values (`PortName`, `DesiredVlanId`, typed `SwitchChangeStep` records such
as `BridgePortPvidChange`) — never a raw RouterOS command string — satisfying NFR1's "no public API
accepts raw command strings" requirement. A second, disjoint transport allowlist,
`RouterOsWriteCommands.Allowlist`, is checked before any socket I/O in a new `RouterOsWriteApiClient`;
`RouterOsReadCommands.Allowlist` is untouched. The write allowlist is exactly six commands: bridge-port
print/set, bridge-vlan print, and scheduler print/add/remove — no reboot, user, firewall, or arbitrary
script-execution command is ever reachable.

**Shared transport hardening.** The connect/TLS/cert-pin (ADR 0019)/login/wire-cap-respecting sentence
reading previously embedded in `RouterOsApiClient` was extracted into an internal
`RouterOsApiConnection`, composed by BOTH `RouterOsApiClient` (read) and the new `RouterOsWriteApiClient`
(write). This is single-source-of-truth by construction, not copy-paste: a future cert-pin fix applies
to both transports automatically. `RouterOsApiClient`'s public/internal shape and its allowlist
chokepoint are unchanged — proven by the existing `SafetyBoundaryTests`/`TlsCertificateValidationTests`/
`ReaderCapsTests` passing without modification.

**Confirmed-commit mechanism: a device-side scheduler job, not Safe Mode.** RouterOS's interactive
"Safe Mode" is session-scoped: it reverts on a *dropped connection*, but a caller that successfully
disconnects (or the API session that the driver's own connection happens to hold) can still "confirm" it
away, and Safe Mode does nothing to protect against a control-plane crash that leaves the connection
technically alive. A `/system/scheduler` one-shot job is window-based and independent of the API
connection's lifetime — it fires whether the control plane process is dead, the network path to it is
gone, or the operator simply walks away — which is the stronger reading of the story's D3 constraint.
The flow is: (1) validate the VLAN id (1–4094) before any I/O; (2) read before-state (PVID + minimal
untagged-membership subset) and fail fast on a missing/ambiguous port or an unconfigured VLAN
(auto-creating the VLAN is out of scope); (3) short-circuit to a no-op when the desired PVID already
matches, with zero device writes (AC2/NFR3); (4) for a dry-run, return the plan and a before/after
preview with zero writes; (5) otherwise, ARM the scheduler job FIRST — a one-shot job whose `on-event` is
a FIXED, driver-owned script template with only the already-validated port name and the already-observed
before-PVID substituted (never caller free-text, keeping NFR1 intact even though a RouterOS scheduler
job is inherently a script string) — THEN apply the `/set`; (6) verify by reading the port back; a
mismatch means the change is NOT confirmed and the armed job self-reverts once its window elapses; (7) on
a successful verification, remove the scheduler entry — this is the confirm signal — and mark the
outcome `Applied`/`Confirmed=true`.

**Happy path confirms synchronously.** `SetAccessVlanAsync` always performs apply→verify→confirm within
a single call; the 30-second (default, configurable) window only ever matters for the FAILURE path
(a crash between apply and confirm, or a verification mismatch), so it does not conflict with NFR4's
sub-5-second P95 for the happy path. An internal `BeginChangeAsync`/`ConfirmChangeAsync` seam (mirroring
the codebase's existing `internal RouterOsApiClient(...)` test-seam convention, via `InternalsVisibleTo`)
lets tests call `BeginChangeAsync` and deliberately never call `ConfirmChangeAsync`, exercising the real
rollback path deterministically.

**Scope: PVID only, membership read-only.** The write surface mutates only a port's PVID (access VLAN).
Bridge-VLAN tagged/untagged membership is read (to assert access-VLAN semantics and for verification)
but never written — mutating membership is deferred to a future story, keeping this story's write
surface minimal per NFR1 and its own out-of-scope list.

**Audit is a pure DTO, not persisted here.** `SwitchChangeAuditRecord` is populated on every outcome
(before/after subset, reason code, correlation id, dry-run flag, confirm window, verification result,
timestamps via an injected `TimeProvider`, actor identity) and returned on `SetAccessVlanOutcome.Audit`.
The driver assembly has no EF Core reference and never persists it — the future apply-API (#65) is
responsible for writing it via `TopologyAuditEvent`/`IAuditEventWriter`. Actor identity travels as typed
request data (`ActorType`/`RequestedBy`), not a `ClaimsPrincipal`, respecting the driver layering.

**A second registry, not a widened one.** `ISwitchMutatingDriverRegistry`/`SwitchMutatingDriverRegistry`/
`ISwitchMutatingDriverFactory` mirror the read-only registry's non-reflective, fail-fast-on-duplicate
shape exactly, registered via a new `AddSwitchMutatingDriver<TFactory>()` DI extension. A consumer
holding only `ISwitchDriverRegistry` cannot structurally reach a mutating factory. `Caisson.Api` still
references no driver assembly — `AddMikroTikRouterOsSwitchMutatingDriver()` is not wired into it.

## Consequences

- Every write outcome — including a rejected VLAN id or an unconfigured VLAN — carries full audit
  evidence via `DriverResult.Ok`, at the cost of `DriverResult.Fail` being reserved strictly for
  infrastructure-level failures (a design choice callers must respect: checking `Success` alone is not
  enough to know whether a change was applied — `SetAccessVlanOutcome.ReasonCode` is the source of truth).
- A write-capable RouterOS user needs a materially different (more privileged) policy than the read-only
  discovery user's `read,api,!write,!policy,!sensitive,...` (`docs/routeros-discovery.md`) — an
  operational credential-provisioning change, tracked in `docs/routeros-write.md`, not a code-shape one.
- The confirmed-commit window is a real, if small, exposure: between apply and confirm, the port carries
  the new VLAN with a scheduled revert already armed. This is the intended trade-off — the alternative
  (confirm-then-apply) cannot self-heal a crash between the two steps.
- Membership mutation (moving a port's tagged/untagged VLAN set) is explicitly deferred; a future story
  extending the write surface must add its own bounded allowlist entries and reason codes rather than
  widening this one's.
