# 0014 — Live topology updates: SignalR hub + Redis backplane & pub/sub

Status: Accepted

## Context

Story #9 pushes real-time, read-only topology updates to UI clients: when a discovery run persists a
new snapshot (story #7 ingestion) or a discovery job changes status (story #8), connected clients must
see it live. The dominant force is **multi-instance correctness** — the platform runs multiple API
instances, so a snapshot persisted on instance A must reach a client connected to instance B. Other
forces: the hub must be strictly read-only and role-gated consistent with story-7 RBAC; no secrets may
appear in events; the discovery pipeline must survive a Redis outage (fail-open); and the seam must not
drag SignalR/Redis into the persistence or orchestration layers (ADR-0001 layering).

## Decision

- **Two event contracts** — `snapshot-updated` (rackId, optional jobId, snapshotId, version, a
  counts-only `SnapshotSummary`, timestamp, seq, correlationId) and `discovery-job-status-changed`
  (rackId, jobId, status, previous status, current step, errorCode, timestamp, seq, correlationId), plus
  a minimal `heartbeat`. A polymorphic `TopologyEvent` envelope (`[JsonPolymorphic]` with a `type`
  discriminator and a stable `eventId`) is the authoritative wire format (camelCase, `docs/live-topology-events.md`).
- **Placement in `Caisson.Infrastructure/LiveUpdates/`** (not a new `Caisson.Realtime` project).
  Infrastructure is the lowest layer both `Caisson.Orchestration` and `Caisson.Api` already reference, so
  the `ITopologyEventPublisher` seam and the contracts ship without a new csproj/solution wiring and
  without either layer naming SignalR/Redis. The concrete Redis publisher lives here; SignalR hub and
  backplane wiring stay in `Caisson.Api`.
- **Single channel + rackId + SignalR groups** (story Q1): every event goes to one Redis channel
  `caisson.topology.events` with the rackId in the payload; the hub assigns clients to per-rack SignalR
  groups (`rack:{rackId}`) and dispatches accordingly. There is no per-rack Redis channel.
- **Backplane + pub/sub composed via an exactly-once relay guard.** The story mandates BOTH a SignalR
  Redis backplane AND Redis pub/sub fan-out. Each API instance both subscribes to the pub/sub channel
  AND runs the SignalR backplane, so a naive per-instance relay would double-deliver (N instances × the
  backplane's own cluster fan-out). The relay is therefore guarded by a cluster lock
  `SET caisson:relayed:{eventId} {instanceId} NX EX 30`: only the instance that wins the key issues the
  `IHubContext` group-send, and the SignalR backplane then delivers to that group's members on every
  instance — exactly once. Client-side `(stream, seq)`/`eventId` de-dup is the safety net.
- **Seq strategy.** `snapshot-updated` reuses the DB-guaranteed monotonic per-rack snapshot `Version`
  (unique index `ux_topology_snapshot_rack_id_version`) — free and cluster-consistent.
  `discovery-job-status-changed` allocates a cluster-monotonic seq via Redis `INCR caisson:seq:job:{jobId}`
  behind an `ITopologyEventSequencer` seam (in-process counter is the single-instance/dev fallback),
  because enqueue and run can happen on different instances.
- **Read-only hub RBAC reusing `TopologyRead`.** `TopologyHub` is `[Authorize(Policy = TopologyRead)]`
  (= Admin/Operator/ReadOnly/ServiceAccount), single-sourced with the query APIs. The only invokable
  server methods are `SubscribeToRack`/`UnsubscribeFromRack` (pure group mechanics) — a reflection guard
  test asserts that exact set, so no future mutating method can slip in. Anonymous → 401, recognised-but-
  insufficient role → 403 via the existing fallback policy.
- **Rack-scoping = role gate + rack existence.** `SubscribeToRack` verifies the rack via
  `RackExistsAsync` (the same helper the query controllers use) and, on a miss, throws a client-safe
  `HubException`, joins no group (fail-closed) and writes an audit entry. No per-rack ACL exists in this
  codebase yet; that finer scoping is deferred behind the same seam.
- **Fail-open publishing with a no-op default.** `ITopologyEventPublisher` implementations MUST NEVER
  throw; the Redis publisher logs a structured warning and swallows all faults. `NoOpTopologyEventPublisher`
  is the `TryAdd` default when Redis is unconfigured, so the DB pipeline never hard-depends on Redis and
  existing tests are unchanged. Publish call sites additionally wrap the call belt-and-braces.
- **No secrets in events** (NFR5). Events carry only ids, versions, status, counts, timestamps, seq and
  correlation ids — never host/port/MAC/credentialsRef/graph/raw device data. A reflection/serialization
  guard test enforces it, mirroring the domain no-secrets guard.
- **WebSocket auth via `access_token` query string.** Browsers cannot set the `Authorization` header on
  the WS upgrade, so the JWT bearer options read the token from the `access_token` query parameter for
  requests under `/hubs/topology`.

### Rejected alternatives

- **Backplane-off, per-instance local-only relay** (each instance relays only to its own connections):
  simpler and duplication-free, but drops the explicitly-required SignalR backplane. An explicit AC
  outranks the simplicity preference, so this was rejected in favour of backplane + guarded relay.
- **A separate `Caisson.Realtime` project** for the contracts/seam: rejected to avoid a new csproj and
  to keep ADR-0001 layering intact (Infrastructure is already the shared lowest layer).
- **In-process per-job seq counter**: rejected because enqueue and run can occur on different instances,
  which would break monotonicity and reintroduce a crash-reclaim ordering anomaly.

## Consequences

- The persistence and orchestration layers publish through a dependency-free seam and never reference
  SignalR/Redis. Without a Redis connection string the publisher is a no-op and the SignalR hub runs
  single-instance (no cross-instance relay/heartbeat) — an intentional graceful degradation for dev/CI.
- The exactly-once guard depends on Redis being reachable for relay correctness; a guard misconfiguration
  degrades to at-most a duplicate delivery, absorbed by client-side de-dup — never a user-visible regression.
- A `TopologyMetrics` meter (connected clients, publish failures, relay deliveries) and a `ready`-tagged
  Redis health check give operators visibility (NFR4).
- The real Angular `TopologySignalRService` (reconnection/backoff, 30s stale banner, client-side
  `(jobId, seq)` de-dup) is a **tracked follow-up**: no frontend project exists in the repo, so this
  story delivers the server side plus a documented wire contract and a .NET SignalR client simulation.
