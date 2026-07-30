# 0047 — Network Config UI composition and the additive topology Switches inventory

## Status

Accepted

## Context

Story #168 needed three UI decisions with more than one defensible answer:

1. **VLAN picker shape for the Port Intent editor.** The design references CDK Overlay anchored-combobox
   patterns (`assignee-picker`/`work-item-priority-picker`) as a "gold standard" — but neither component
   exists in this repository; the only two CDK patterns actually present are `topology-search.component.ts`'s
   anchored `CdkConnectedOverlay` combobox and `@angular/cdk/dialog`'s `Dialog` modal (ADR 0034,
   `ApplyConfirmationDialogComponent`). The editor's VLAN selection has no typeahead requirement and must
   structurally prevent picking a non-catalogue VLAN (AC2).
2. **Whether the existing topology graph already exposes "every discovered port on every switch".** The
   Port Intent screen must be driven entirely by discovered inventory, with no manual port/switch
   creation.
3. **Unsaved-changes confirmation primitive**, and how to keep its dependency (CDK Dialog) out of the
   eagerly-loaded root bundle.

## Decision

1. **A native `<select>`** (styled via the existing `cds-form-input` mixin), not a CDK Overlay combobox,
   inside a `@angular/cdk/dialog` modal shell (mirroring `ApplyConfirmationDialogComponent`'s `Dialog.open`
   pattern). Its option list is exactly "Unchanged/Inherit" plus every catalogue VLAN, so a non-catalogue
   selection is structurally impossible — and the native control gives the full interactive/accessibility
   baseline (close on select/outside-click/Escape, keyboard nav, native listbox ARIA, theme-safe states)
   for free, with no anchored-overlay/focus-management code to hand-roll.
2. **The existing `TopologyGraphDto`/`TopologyGraphView` is NIC-centric and insufficient**: a port only
   ever appears via `NicNode.BestAttachment` or the anti-joined `UnmappedPorts` list — a port that is
   only ever a lower-ranked NIC *candidate* (present in `TopologyCandidateMapping` but never anyone's best
   attachment, and therefore excluded from the anti-join) is absent from both. `TopologyGraphProjector`
   gains an **additive** `Switches: SwitchInventoryNode[]` field, computed from the same already-loaded
   `snapshot.Switches[].Ports[]`, threaded through `TopologyGraphDto.Switches` and the frontend
   `TopologyStateService.switches` signal — populated once alongside the existing topology graph
   fetch/live-refresh path, never a second inventory query. Existing NIC-rooted consumers ignore the new
   field entirely (the frontend type marks it optional so every pre-existing graph fixture/test literal
   keeps compiling).
3. **`@angular/cdk/dialog`** for the discard-confirmation (`DiscardChangesDialogComponent`), not
   `window.confirm`, consistent with ADR 0034. The `CanDeactivateFn` guard itself is referenced from
   `app.routes.ts`/`app.routes.prod.ts` via a dynamic `import()` wrapper rather than a static import — a
   statically-imported guard would pull the whole CDK Dialog dependency graph into the eager root routes
   module (verified: this alone pushed the initial bundle over its budget), the same code-splitting
   concern `loadComponent` already solves for every route component.

## Consequences

- Any future per-port-attribute screen (e.g. trunk/LAG authoring) can reuse the same `Switches[].Ports[]`
  inventory rather than re-deriving a NIC-centric projection or adding another API call.
- The native-select choice means no typeahead/search-within-picker exists yet; if a future rack's VLAN
  catalogue grows large enough to need it, that is a deliberate, separate enhancement — not a gap in this
  story's scope.
- Any other route-level guard/resolver that pulls in a heavy dependency (CDK, a chart library, etc.)
  should follow the same dynamic-import wrapper pattern established here, rather than importing it
  statically into the routes module.
