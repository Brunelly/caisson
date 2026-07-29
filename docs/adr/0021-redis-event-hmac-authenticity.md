# 0021: HMAC-authenticated Redis events, with rack/seq plausibility and a fail-closed connection guard

## Status

Accepted

## Context

Security review `security-review-5` (findings #2, #30) found two related gaps in the live-updates pub/sub
path (ADR 0014). First, `TopologyEventSerialization.Deserialize` only caught `JsonException`; a payload
with no `type` discriminator (or an unrecognised one) makes `System.Text.Json`'s polymorphic
deserialization throw `NotSupportedException` instead, which `RedisTopologyEventSubscriber.RelayAsync`
called *before* entering its own try/catch — an untrusted or malformed channel message could crash the
per-instance relay's `BackgroundService`. Second, and more fundamentally, nothing authenticated the
channel: any process able to `PUBLISH` to the configured Redis channel — a misconfigured ACL, a
misrouted publish from something else sharing the instance — could inject an event that every connected
client's UI would render as legitimate. The Redis connection itself was also never checked for a
password or TLS.

## Decision

- **Robust decode (finding #30)**: `TopologyEventSerialization.Deserialize` now catches `NotSupportedException`
  alongside `JsonException`, both answered the same way — `null`, never a throw. `RelayAsync` moved the
  whole decode-and-verify sequence inside its existing try/catch, and a null decode is now logged and
  counted (`TopologyMetrics.RecordDecodeFailure`) rather than silently swallowed.
- **HMAC authenticity (finding #2)**: a new `TopologyEventAuthenticity` (`Caisson.Infrastructure.LiveUpdates`)
  appends an HMAC-SHA256 tag (full 32-byte output, hex-encoded — this isn't URL-carried like a
  `CursorCodec` cursor, so there's no reason to truncate it) to the serialized envelope before publish,
  and verifies + strips it before decode on the subscriber side, via `CryptographicOperations.FixedTimeEquals`.
  Key resolution mirrors `CursorCodec` exactly: `CAISSON_REDIS_HMAC_KEY` env var first, else a fixed
  documented development key, UNLESS `ASPNETCORE_ENVIRONMENT=Production` is explicitly set, in which case
  an unset key throws rather than falls back. A missing/invalid tag is dropped, logged, and counted
  (`TopologyMetrics.RecordRelayRejection`) — never thrown, since this runs inside a Redis pub/sub
  fire-and-forget callback. Publish-side signing failures are already absorbed by
  `RedisTopologyEventPublisher`'s existing fail-open catch (AC4/NFR3) — no special-casing needed there.
- **Plausibility, defense in depth (finding #2)**: `RedisTopologyEventSubscriber` now takes an
  `IServiceScopeFactory` and, for the two event types that carry a `(RackId, Seq)` pair, checks the rack
  id against a 30-second-TTL cache of known rack ids (refreshed from `CaissonDbContext.Racks`) and rejects
  a seq that has jumped more than 10,000 ahead of the last one observed for that rack. Both checks fail
  **open** on their own fault (an unreachable DB skips the rack-id check; there's no analogous fault mode
  for the in-memory seq map) — this is defense in depth sitting behind the HMAC check, not the primary
  control, so a transient DB blip must not stop correctly-signed events from reaching clients.
  `HeartbeatEvent` carries neither field and always passes.
- **Fail-closed connection guard**: a new `RedisEventAuthenticityStartupGuard` (mirroring
  `JwtAuthorityStartupGuard`'s shape, called from `Program.cs` where `builder.Environment` is available)
  parses the resolved `CAISSON_REDIS` connection string and refuses to start outside Development/Testing
  when the Redis connection has neither a password nor TLS — only when the Redis-backed live-updates path
  is actually enabled; the no-Redis dev/CI fallback (ADR 0014) is untouched.

## Consequences

- Every API instance must now be provisioned with the same `CAISSON_REDIS_HMAC_KEY` (mirroring the
  existing requirement that they share `CAISSON_CURSOR_HMAC_KEY` and the Redis connection itself) — a
  deployment topology already implied by ADR 0014's cross-instance backplane, so this adds no new
  operational surface, only a new secret to provision alongside the ones that already exist.
- A key rotation invalidates in-flight (unconsumed) messages for the rotation window, same as a
  `CursorCodec` key rotation invalidates outstanding cursors — acceptable, since a dropped live-update
  event is recovered by the client's existing REST reconciliation path (deferred to Step 5, finding #2's
  client half), never a correctness issue.
- `RedisEventAuthenticityStartupGuard` is a genuine new production requirement: a real deployment's Redis
  must be provisioned with a password or TLS (ideally both) before this guard will allow the API to start.
  This is the intentional, safest variant per the task's own instruction to prefer hardening over silently
  skipping a finding — noted here as a deployment-facing deviation for the PR description.
- The rack-id/seq plausibility checks are heuristic, not cryptographic — a forward seq jump of exactly
  10,000 would still pass, and a compromised holder of the HMAC key defeats all three checks. They exist
  purely to catch bugs and non-malicious anomalies (e.g. a stale/replayed message) cheaply, behind the
  HMAC check that is the actual security boundary.
