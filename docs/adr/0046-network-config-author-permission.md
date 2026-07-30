# 0046 — A dedicated NetworkConfigAuthor permission, not a reuse of Operator

## Status

Accepted

## Context

Story #168 must "gate authoring behind an appropriate permission (formalised in #174)". The write surface
is narrow (PUT the combined VLAN-catalogue/port-intent draft, and the `/validate` stub) and view access
must stay open to every existing read role, including Read Only — the story's AC1 explicitly requires a
Read Only user to see the catalogue and port intent, just not edit it. Two shapes were viable: reuse
`CaissonRoles.Operators` (Admin/Operator) for the write endpoints, or add a new, independently-revocable
permission distinct from every existing role, mirroring the precedent `CaissonRoles.DriftApply` (story
#65, ADR 0032) already established for the API's first write path.

## Decision

Add `CaissonRoles.NetworkConfigAuthor` as a new elevated permission, deliberately excluded from
`CaissonRoles.All` and `CaissonRoles.Operators` — an Operator or even an Admin who has not been
specifically granted it is rejected with 403 on PUT/validate, exactly as `DriftApply` already behaves for
the apply endpoint. It is added to `CaissonRoles.AllMappableTargets` so an org can map an Entra
app-role/group onto it without it ever being implied by a broader role. GET stays behind the ordinary
`AuthorizationPolicies.TopologyRead` policy (`CaissonRoles.All`), so a Read Only user can view authored
intent without holding the elevated permission. The frontend mirrors this exactly: a UX-only
`NetworkConfigPermissionService`/`hasNetworkConfigAuthorPermission` gate (matching
`DriftPermissionService`) hides — not merely disables — every mutating control for a principal lacking
the claim, with the server remaining the sole enforcement point.

## Consequences

- Authoring network intent and applying a drift correction are now two independently-grantable
  permissions; an org that wants "the same people who can apply drift can also author intent" must map
  both explicitly — there is no implied hierarchy between them, by design (each captures a distinct
  operational risk).
- The read-only reflection guard (`ReadOnlyGuardTests`) and the RBAC integration-test matrix both needed
  updating to recognise `NetworkIntentController`'s PUT/validate as allow-listed, policy-gated writes —
  the same mechanical step every future write endpoint will need.
- Story #174 (RBAC formalisation) can add `NetworkConfigAuthor` to its config-driven role-mapping
  documentation without any further code change here — the mappable-targets list is already wired up.
