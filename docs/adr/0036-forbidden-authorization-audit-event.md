# 0036 — Forbidden authorization results persist an audit event

## Status

Accepted

## Context

Story #68 AC3 requires that an apply attempt by a principal lacking the `DriftApply` permission produces
"an authorization failure audit/log entry ... including rackId, driftItemId, and correlationId (without
secrets)". `DriftApplyController.Apply` is decorated with `[Authorize(Policy = AuthorizationPolicies.
DriftApply)]` (ADR 0032) — ASP.NET Core's authorization middleware short-circuits a Forbidden result BEFORE
the controller action ever runs, so `DriftApplyController`'s own `_audit.WriteActionAsync("drift.apply.job.
created", ...)` call is unreachable on that path. The only place a Forbidden result is observable at all is
`ForbidLoggingAuthorizationResultHandler` (ADR 0032 decision 1), which today only writes a structured
`LogWarning` (subject/path/correlationId) — no `TopologyAuditEvent` row, no `rackId` as a discrete field, no
`driftItemId` at all. This is a genuine gap, not a design choice: AC3 cannot be satisfied without a change
somewhere in the authorization pipeline, since a declarative `[Authorize]` gate — the same idiom every other
write endpoint in the API relies on — structurally prevents a controller-level audit write on the forbidden
path.

## Decision

`ForbidLoggingAuthorizationResultHandler.HandleAsync` additionally persists a `TopologyAuditEvent` via
`IAuditEventWriter.WriteActionAsync` (resolved per-request from `context.RequestServices`, since the
handler itself is a singleton but `IAuditEventWriter` is scoped) whenever `authorizeResult.Forbidden` is
true — the SAME condition that already triggers the existing log line, so both are (or fail to be) present
together for every policy, not just `DriftApply`. Concretely:

- **Action/result**: `"authorization.forbidden"` / `"403"`.
- **rackId**: read from `context.Request.RouteValues["rackId"]` — present for every rack-scoped policy
  check (every route in the API that carries an `{rackId:guid}` segment), `null` otherwise (e.g. a
  rack-independent endpoint).
- **correlationId**: the SAME `ICorrelationContext.CorrelationId` the existing log line already uses.
- **driftItemId**: a narrowly-scoped, best-effort peek at the still-unbound request body — ONLY performed
  when the resolved `AuthorizationPolicy` requires the `DriftApply` role (matched structurally via
  `RolesAuthorizationRequirement.AllowedRoles`, not a policy-name string, since `HandleAsync` only receives
  the resolved `AuthorizationPolicy` object) AND the request's `Content-Type` is JSON. The body is read via
  `HttpRequest.EnableBuffering()` + an explicit `Position = 0` reset before AND after parsing, so a
  downstream component (nothing, for a genuinely Forbidden result, but the code makes no assumption about
  that) still sees an intact, rewindable stream. A missing, empty, non-JSON, or malformed body — or any
  other exception — is swallowed; `driftItemId` is simply omitted, never surfaced as an error.
- **Never a 500**: the entire audit-write path (rackId/driftItemId resolution + the `WriteActionAsync` call
  itself) is wrapped in a single best-effort `try/catch` that swallows every exception. A forbidden request
  reaching this handler must always still receive its 403 from the wrapped `AuthorizationMiddlewareResultHandler`
  — auditing is additive hardening, never a new failure mode.
- **Once per request**: the existing structured `LogWarning` is kept exactly as-is (unchanged), so both the
  log line and (best-effort) the audit row fire together, satisfying NFR3's "authorization failures are
  logged once per request with 403 outcome."

Tests: `tests/Caisson.Api.IntegrationTests/RbacTests.cs` gains four focused cases (persisted audit event
with rackId/correlationId on a generic 403; a malformed-body 403 that still returns 403, not 500, with no
`driftItemId`; an absent-body 403 that still returns 403; a well-formed-body 403 that DOES recover
`driftItemId`). `tests/Caisson.VirtualRack.IntegrationTests/DriftApplyRbacEndToEndTests.cs` proves the same
shape end-to-end against the real virtual-rack harness (Operator-without-`DriftApply` → 403, zero
`DriftApplyJob` rows, an `authorization.forbidden` audit event with `rackId`/`driftItemId`/`correlationId`
and no credential-shaped strings in `detailsJson`) and adds the NFR5 concurrency proof (two concurrent
applies for one `driftItemId` yield the SAME `jobId`, one job row, and exactly one device write).

## Consequences

- This is generic, cross-cutting hardening — it fires for a Forbidden result on ANY policy in the API
  (`TopologyRead`, `DiscoveryTrigger`, `ScheduleManage`, `DriftApply`), not only drift-apply. Every existing
  403 case across the suite now also produces an audit row; no test needed to change because of this (audit
  writes are additive, never block the response).
- `driftItemId` recovery depends on the request body still being at position 0 and unconsumed when
  authorization middleware runs — true for every current endpoint (authorization always runs before model
  binding in the ASP.NET Core pipeline) but would break silently (degrading to "no driftItemId", never a
  crash) if a future custom middleware started consuming the body earlier in the pipeline.
- The `RolesAuthorizationRequirement`-based policy match is coupled to `AuthorizationPolicies.DriftApply`
  being implemented as `RequireRole(CaissonRoles.DriftApply)` (ADR 0032). A future policy shape change (e.g.
  a claims-based requirement instead of a role) would need a matching update here, or the body-peek would
  silently stop firing for that policy — an acceptable trade-off since the peek is best-effort by design.
