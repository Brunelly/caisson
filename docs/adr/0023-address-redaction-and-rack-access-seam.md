# 0023: Role-based address redaction and a per-rack access seam (allow-all today)

## Status

Accepted

## Context

Security review `security-review-5` (finding #29) found that `TopologyRead` grants all four roles
(including `ReadOnly`) unredacted access to every NIC MAC, switch `managementIp`, server `bmcAddress`
and LLDP `mgmtAddress`, with no per-rack ACL anywhere in the codebase — any recognised principal can read
any rack. ADR 0012 already documents this as a deliberate, single-tenant internal control-plane posture,
so this finding is genuinely low severity, but the review asked for two additive changes: field-level
redaction by role, and a resolvable (if not yet enforced) per-rack authorization seam.

## Decision

- **Redaction**: `ContractMappers.RedactManagementFields` nulls out `managementIp`/`bmcAddress`/
  `mgmtAddress` in the entity latest-fields dictionary for a non-privileged (`ReadOnly`) caller; NIC MACs
  in the topology graph are OUI-preserved/NIC-masked (`ContractMappers`'s new MAC redaction helper) for
  the same callers, so SPA search/display by vendor still works without exposing the individually
  identifying full address. Operator/Admin continue to see full values.
- **`IRackAccessPolicy` seam**: a `CanReadAsync(ClaimsPrincipal, Guid rackId)` interface, resolved via
  `HttpContext.RequestServices` in `ReadOnlyControllerBase`/`DiscoveryControllerBase` (avoiding a
  constructor-signature change across every existing controller) and via constructor injection in
  `TopologyHub.SubscribeToRack`. The shipped `AllowAllRackAccessPolicy` is genuinely additive — it changes
  no current behaviour. A denial (from a future implementation) is surfaced as the same 404 as a missing
  rack, never 403, so rack existence is never an oracle for a caller without access to it.

## Consequences

- **Deviation, consistent with ADR 0012's documented single-tenant posture**: full per-rack ACL
  enforcement is deferred — only the redaction and the allow-all seam ship in this pass. A future
  restriction is now a one-class (`IRackAccessPolicy` implementation) change instead of a
  controller-by-controller retrofit.
- This is a contract change the Angular client must follow: a `ReadOnly` session now receives `null`
  management-plane addresses and masked MACs where it previously saw full values, and must render that
  gracefully (search/graph/details panel) rather than treating `null`/masked as an error — tracked as
  part of the Step 5 web-hardening work in this same story.
- The diff-history JSON payload (`TopologyEntityDiff.DiffPayloadJson`) is **not** redacted by this pass —
  only the entity detail endpoint's live "latest fields" dictionary is. Redacting embedded historical JSON
  (already-persisted per-snapshot diffs) is a larger change deferred as a follow-up; noted here as a known
  gap rather than silently left unmentioned.
- Cross-reference: ADR 0012 (the original TopologyRead role grant this redaction narrows) and ADR 0011
  (the append-only diff/audit tables this pass does not touch).
