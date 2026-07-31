# Adding a pre-flight validation rule

This walks through adding a new rule to the network-config **pre-flight validation** engine
(`Caisson.Domain.NetworkConfig.Preflight`). Pre-flight validation runs an authored candidate
(VLAN catalogue + per-port access-VLAN intents) through an ordered, deterministic pipeline before a Git
PR can be created. See [ADR 0052](adr/0052-preflight-validation.md) for the design decisions behind the
shapes described here.

The engine is pure and EF-free (`PreflightValidator.Validate(vlanCatalogue, portIntents, RackInventory,
rackId)`), so every rule is a fast, DB-free unit test and the whole pipeline is side-effect free (NFR3).

## The three stages

Rules live in one of three ordered stages; add yours to the stage that matches its inputs:

1. **schema** (`schema.*`) — bounds on the authoring model itself (VLAN id range, name/description
   length, required fields). These are **not written here twice**: they come from the shared
   `NetworkIntentValidator` (which sources its bounds from `DesiredStateSchema`), and
   `PreflightValidator` translates each `(field, message)` onto a `PreflightIssue`. To add a schema
   bound, add it to `NetworkIntentValidator` and map its field in
   `PreflightValidator.TranslateValidatorIssue` — never introduce a second copy of the rule.
2. **semantic** (`semantic.*`) — rack-scoped uniqueness and resolvable references (duplicate VLAN ids,
   duplicate/conflicting port intents, unknown switch/port, VLAN-not-in-catalogue, missing topology).
   Intra-payload semantics come from `NetworkIntentValidator` (translated); topology-resolution rules
   are added to `PreflightValidator.AddTopologyIssues`.
3. **safety** (`safety.*`) — non-blocking guardrails that **only run when no blocking error exists**
   (e.g. a change to a management/uplink port). Add these to `PreflightValidator.AddSafetyIssues`.

Errors (`PreflightSeverity.Error`) block PR creation; warnings (`PreflightSeverity.Warning`, including
safety notices) are non-blocking and require explicit acknowledgement at PR time.

## Conventions every rule must follow

- **Stable code.** Add a `const` to `PreflightCodes` in the right `schema.*`/`semantic.*`/`safety.*`
  namespace. Codes are the automation/UI contract — once shipped they never change.
- **Canonical field path (RFC 6901).** Build the `FieldPath` with `JsonPointer.Build(...)` — e.g.
  `/vlanCatalogue/2/id`, `/portIntents/5/accessVlanId`. Never hand-concatenate; `JsonPointer.Escape`
  handles `~`/`/` in tokens.
- **UI path.** Also set `UiPath` to the bracket/dot editor path the Angular components map to a control
  (`vlanCatalogue.vlans[2].id`, `ports["switchA/ether5"].accessVlanId`). This is what the
  `ValidationIssuesPanel` uses to focus/scroll the offending control.
- **EntityRef.** Attach an `EntityRef` (rack/switch/port/vlan) via the `EntityRef.Rack/Vlan/Switch/Port`
  factories so the issue is addressable by automation and re-runs.
- **User-friendly message.** `Message` is display-ready (AC1). A topology/reference error must suggest an
  action ("select a known port or refresh topology"). A safety warning must carry port identity + reason
  and set `Details["reason"] = "heuristic-derived"` when the classification is a heuristic.
- **Deterministic ordering.** Emit issues with a `SortKey(group, index, code, fieldPath)` so the issue
  set, the field paths, and (via `ValidationRunToken`) the `validationRunId` are identical across
  re-runs for identical input + topology (NFR3, AC4). Sort by VLAN-catalogue order, then port-intent
  order, then code.
- **No 500s for validation failures.** A rule reports a `PreflightIssue`; it never throws. Missing
  topology is an actionable `semantic.topologyUnavailable` error, not an exception.
- **Audit constraints.** Nothing in a rule may cause a DB write or leak the candidate/secret material.
  The controllers audit counts/outcome/snapshotId only.

## Required tests per new rule

Add cases to `tests/Caisson.Domain.Tests/NetworkConfig/Preflight/PreflightValidatorTests.cs`:

- the rule fires on the offending input, with the exact `Code`, `Severity`, `FieldPath` and `EntityRef`;
- it does **not** fire on the valid/edge case (e.g. a safety rule does not warn on an unchanged port);
- for safety rules: it is suppressed while any blocking error exists;
- ordering/determinism is unaffected (repeat-run equality).

If the rule depends on new observed signals, extend `RackInventoryProjector` and its tests
(`tests/Caisson.Infrastructure.Tests/Persistence/Shaping/RackInventoryProjectorTests.cs`), and add an API
integration case to `DesiredStatePreflightApiTests` / `DesiredStatePrApiTests` proving RBAC, the
counts-only audit, and side-effect-freedom still hold.
