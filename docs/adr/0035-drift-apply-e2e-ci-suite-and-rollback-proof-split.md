# 0035 — Drift-apply E2E CI suite: rollback proof split, determinism, and terminal-status shape

## Status

Accepted

## Context

Story #68 asks for a deterministic, simulation-only CI suite proving drift detection, RBAC-gated apply,
and confirmed-commit auto-rollback end-to-end (AC1–AC6). It is a CI-proof story building entirely on
already-shipped components (ADR 0029 drift identity, ADR 0031 RouterOS safe-apply, ADR 0032 drift-apply
orchestration/RBAC) — no new production behaviour is in scope. Two genuine design problems had to be
resolved to write the rollback and severity assertions honestly, and one determinism hazard was found and
fixed while wiring the harness.

## Decision

**1. Rollback is proven at two complementary layers, not one.** The real
`RouterOsSwitchMutatingDriver.SetAccessVlanAsync` composes `BeginChangeAsync`+`ConfirmChangeAsync`
synchronously in a single call by design (ADR 0031's "can't brick the un-bricker" safety boundary) — the
`DriftApplyOrchestrator` only ever calls that public one-shot method, so it structurally can never withhold
confirmation through the real driver. Rather than widening `InternalsVisibleTo` to give the orchestrator
(or a test) access to the internal `BeginChangeAsync` seam, or adding a new simulator fault-injection
primitive:
- **Driver layer** (already proven, referenced not duplicated): `SetAccessVlanIntegrationTests` drives
  `BeginChangeAsync` directly against the simulator's virtual clock (`AdvanceTime`/`FireDueRollbacks`),
  proving the real device-level rollback mechanism with zero wall-clock wait.
- **Orchestration layer** (new, `DriftApplyRollbackEndToEndTests`): a scripted
  `ScriptedWithheldMutatingDriverFactory`/`ScriptedWithheldMutatingDriver` is registered ADDITIVELY next to
  the real `RouterOsSwitchMutatingDriverFactory`, under a distinct `("MockWithheld", null, Ssh, ...)`
  descriptor (mirrors mcp-tooling's existing `DriftApplyRunner` scenario-2 pattern) — only a rack created
  under the new `RackScenario.WithheldRollback` resolves to it. This proves the *orchestrator's* handling of
  an `AutoRolledBack` outcome (job reaches `Failed`, exactly one device call, terminal audit) without
  fighting the driver's own safety boundary.

**2. The scripted double mutates REAL simulator state, not a hardcoded fake.** mcp-tooling's precedent
returns a hardcoded `before` PVID of `5`, unrelated to any actual device state. This suite's
`ScriptedWithheldMutatingDriver` instead reads the real in-process `RouterOsApiSimulator`'s current PVID via
a new test-only seam, `RouterOsApiSimulator.SetPortAccessVlanForTest`, applies the desired VLAN, then
immediately reverts it to the original value before returning — synchronously simulating what the real
device's own armed scheduler job does once its window elapses unconfirmed. This is the refinement that
makes "a subsequent discovery snapshot reflects the rollback" genuinely provable: a fresh discovery job
against the SAME simulator instance observes the ACTUAL reverted PVID, not a value the test double merely
claimed.

**3. A matching read-side double keeps discovery real for the withheld-rollback rack.**
`ISwitchMutatingDriverRegistry`/`ISwitchDriverRegistry` both resolve by `(Vendor, Model, ConnectionKind)`
from the SAME `DeviceDefinition`. Giving one rack's switch device a distinct `Vendor` (`"MockWithheld"`) so
its WRITE path resolves to the scripted double also means its READ path would otherwise fail to resolve
(`DriverNotFound`) — the real `RouterOsSwitchDriverFactory` only answers `"MikroTik"`. `MockWithheldReadDriverFactory`
is registered under the same distinct vendor and simply delegates every call to a real
`RouterOsSwitchDriverFactory` instance — it is NOT a double, only a re-labelled pass-through — so discovery
for this one rack still talks to the real simulator over the real RouterOS protocol.

**4. Terminal-status shape: `Failed` + `deviceReasonCode=AutoRolledBack`, not a distinct `RolledBack`
status.** `DriftApplyOrchestrator.FinalizeFromDeviceOutcomeAsync` completes the job only for
`Applied`/`NoOpAlreadyDesiredState`; every other reason code — including `AutoRolledBack` — fails it with
`ErrorCategory=DeviceRejected`. The test suite asserts this actual shape rather than inventing a
`RolledBack` job status the domain model does not have.

**5. Severity: assert the shipped value (`High`), do not change production code to match the story's
illustrative `Medium`.** `DriftSeverityRules` deterministically maps `AccessVlanMismatch → High`. Story
#68 AC2's "e.g. Medium" is an explicit example, not a requirement, and this is a proof story: the CI suite
asserts whatever `DriftSeverityRules` actually ships and NEVER modifies the production mapping to match the
story text — a behaviour change to severity would ripple into the Angular drift UI/audit views (story #67)
and is out of scope for a CI-proof story. Flagged here per this decision's own review; no production
severity code was touched.

**6. Determinism fix found while wiring the harness: a SEPARATE stateful simulator instance, not a mutated
shared one.** `RouterOsProfileRenderer.RenderStateful()` seeds a `SimulatorSwitchState` alongside the same
byte-identical discovery replies `Render()` already produces. `VirtualRackApiFactory` constructs this as a
SECOND `RouterOsApiSimulator` instance (`_writeCapableSwitchSimulator`), never mutating the original
`_switchSimulator` used by every existing detection-only test — those tests are provably unaffected. Only
racks created under `RackScenario.DriftApplyCapable`/`WithheldRollback` are pointed at it. The seeded VLAN
table additionally registers the drift fixture's mismatch target VLAN (99, `DesiredStateYamlRenderer.
MismatchedVlan`) with empty membership, since the write driver's pre-apply check
(`IsVlanConfigured`) fails every apply with `VlanNotConfigured` otherwise.

**7. Determinism fix: device-mutating tests reset their own baseline.** The write-capable simulator is a
single shared instance across every test in the `VirtualRackCollection` (tests in one xUnit collection run
sequentially, but in an order xUnit does not guarantee stays fixed across runs). A drift-apply test that
sets `ether1`'s PVID to a new value leaves that mutation visible to the NEXT device-mutating test that
happens to run in the same process, if that test assumed a specific starting PVID. Every device-mutating
test (`DriftApplyEndToEndTests`, `DriftApplyRollbackEndToEndTests`, the NFR5 concurrency test) now calls the
new `VirtualRackApiFactory.ResetSwitchPortAccessVlanForTest` seam before seeding, forcing its own known
baseline rather than relying on execution order.

## Consequences

- The rollback audit trail (`drift.apply.job.failed`'s `detailsJson`) does NOT currently carry a distinct
  "confirm window seconds" field — `DriftApplyJobRunner.BuildTerminalAuditDetails` only persists
  `deviceReasonCode`/`deviceConfirmed`/`beforeState`/`afterState`/`switchDeviceKey`/`portName`/
  `desiredVlanId`/`errorCategory`/`errorCode`/`driftItemId`/`permission`. This is a genuine, if minor, gap
  against story #68 AC5's "confirm window" wording — flagged here rather than silently asserted-around; a
  future story extending the audit shape could add it. The test suite asserts only the fields the code
  actually persists.
- `ScriptedWithheldMutatingDriver`'s `Before`/`After` states both report the ORIGINAL PVID (numerically
  equal) — this is correct, not a bug: after a genuine rollback the device ends up back where it started,
  and the "attempted" VLAN is separately visible via the job's own `desiredVlanId`/`DesiredVlanId` field.
- A future drift-apply E2E test that mutates `ether1`'s (or any write-capable rack's) real device state
  MUST call `ResetSwitchPortAccessVlanForTest` first, or explicitly document why it does not need to —
  otherwise it inherits an undocumented dependency on test execution order.
- The two-layer split means a regression in the orchestrator's `AutoRolledBack` handling and a regression
  in the driver's own confirmed-commit timer are caught by two independent tests; neither alone proves the
  other layer still works.
