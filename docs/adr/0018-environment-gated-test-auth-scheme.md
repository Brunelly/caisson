# 0018: An environment-gated, fail-closed test-auth scheme for the Playwright e2e smoke

## Status

Accepted

## Context

Story #10 wired `web/e2e/topology.smoke.spec.ts` behind the `E2E_ENABLED` repo variable, but it has
never actually run against a live `Caisson.Api` in CI: the API's only authentication path is JWT bearer
against a real Entra tenant, which this project's CI cannot self-provision (ADR 0016 solved the
equivalent problem for a real-browser Angular harness by faking the wire client-side; there is no
analogous seam on the API side — `TestAuthHandler` in `Caisson.Api.IntegrationTests` only exists inside
`WebApplicationFactory`'s in-process test host, not a Kestrel process a browser can actually talk to).

Story #11 needs the smoke to authenticate against a **real, separately-running** `Caisson.Api` process
in CI. Adding *any* bypass to a production authentication path is inherently risky, so this was
explicitly scoped and approved for this story with non-negotiable hardening constraints: a default-false
config flag; a fail-closed startup guard that makes the host **refuse to boot** (not merely reject
requests) if the flag is set outside Development/Testing; the flag never committed as enabled in any
non-Development appsettings file; a fixed, minimum-privilege synthetic principal (never Admin/Operator);
and a prominent startup warning whenever the scheme is active.

## Decision

Add `Testing:EnableTestAuth` (default `false`, documented only in `appsettings.json` — never set to
`true` in any committed non-Development file). `Program.cs` reads it immediately after
`builder.Environment` is available, before any service registration, and calls
`TestAuthStartupGuard.Validate(builder.Environment, enableTestAuth)`, which throws
`InvalidOperationException` — refusing to boot — whenever the flag is true under any environment other
than Development or Testing.

When enabled, `TestAuthenticationHandler` (scheme name `CaissonTestAuth`) becomes the **default**
authenticate/challenge scheme (the JWT bearer registration itself is otherwise byte-for-byte unchanged
and stays registered), so the existing fallback `RequireAuthenticatedUser` + `RequireRole` policies
resolve its synthetic principal with zero controller changes. This default-scheme swap was chosen over
an `AddPolicyScheme` forwarder with a shared token: it needs no shared secret, requires no token
generation on the Angular side (a fake `OidcSecurityService.getAccessToken()` can return any static
string — the API ignores its content entirely once this scheme is active), and is simpler to reason
about because there is exactly one principal, ever: subject `caisson-ci-e2e`, holding only
`CaissonRoles.ReadOnly` — both fixed code constants, never sourced from a header, query string, or
config value, so there is no way to mint a more privileged identity through this seam.

A prominent `LogWarning` fires at startup whenever the scheme is active, mirroring the existing
Redis/Realtime startup-visibility block. CI supplies the flag solely via the `Testing__EnableTestAuth=true`
environment variable alongside `ASPNETCORE_ENVIRONMENT=Testing`.

## Consequences

- The Playwright smoke can authenticate against a live `Caisson.Api` in CI with no real Entra tenant and
  no token-minting logic on either side — the Angular e2e build just needs to satisfy `roleGuard`
  client-side and attach *some* bearer token so the request reaches the API at all.
- The synthetic principal is permanently read-only; any test that needs to trigger discovery cannot use
  this scheme — that gap is intentional (least privilege) and unrelated to what the smoke actually needs.
- A wrong `ASPNETCORE_ENVIRONMENT`/flag combination in a real deployment is caught at boot, not silently
  live in production — verified by a test that constructs the host with `UseEnvironment("Production")`
  and asserts host construction itself throws.
- This is a distinct, unrelated seam from `Caisson.Api.IntegrationTests`' `TestAuthHandler`, which only
  exists inside `WebApplicationFactory`'s in-process test host and is not reachable by a real HTTP client;
  neither scheme's existence weakens the other's boundary.
