# ADR 0048: App-shell rack discovery and selection

## Status

Accepted

## Context

The root SPA route had no owner, so it could neither discover observed racks nor start the existing rack-scoped topology flow. Rack identities must also remain hidden when rack access policy denies them.

## Decision

Expose an ACL-filtered, deterministically ordered observed-rack catalogue and cache each successful catalogue load in a root-provided client service. A guarded landing component chooses the first accessible rack; `TopologyPageComponent` remains the sole owner of topology REST and SignalR orchestration.
Defer the selector's CDK Overlay directives until immediately after initial render so overlay behavior does not consume the production initial-bundle budget.
The explicit-origin SPA CORS policy permits credentials because the SignalR browser client negotiates with credentials mode `include`.

## Consequences

The root route can represent loading, empty, authorization, and retry states without blocking bootstrap, and the top bar can reuse the same catalogue without duplicate requests. Catalogue reads add one audit event per request; access-policy checks remain per candidate rack.
The selector briefly renders a disabled loading placeholder while its overlay chunk loads.
