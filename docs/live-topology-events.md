# Live topology events — wire contract

This document is the authoritative wire contract for the live topology update pipeline (story #9,
[ADR 0014](adr/0014-live-topology-updates-signalr-redis.md)). It is what a future Angular
`TopologySignalRService` (or any client) consumes. The .NET types live in
`Caisson.Infrastructure/LiveUpdates/` and are serialized by `TopologyEventSerialization` (camelCase,
polymorphic `type` discriminator).

## Transport

- **Hub endpoint:** `GET /hubs/topology` (SignalR, WebSockets).
- **Auth:** an OIDC/Entra JWT bearer token. Browsers cannot set the `Authorization` header on the
  WebSocket upgrade, so the token is passed as the `access_token` query-string parameter
  (`/hubs/topology?access_token=<jwt>`); the standard header also works for non-WS transports.
- **Authorization:** the `TopologyRead` policy (Admin, Operator, ReadOnly, ServiceAccount). Anonymous →
  401; authenticated without a recognised role → 403. The hub is strictly read-only.

## Server → client methods

| Method | Payload | Meaning |
|--------|---------|---------|
| `SnapshotUpdated` | `snapshot-updated` event | A new snapshot was persisted for a rack. |
| `DiscoveryJobStatusChanged` | `discovery-job-status-changed` event | A discovery job changed status. |
| `Heartbeat` | `heartbeat` event | Liveness beat, emitted every 10s. |

## Client → server methods (the only two)

| Method | Args | Behaviour |
|--------|------|-----------|
| `SubscribeToRack` | `rackId: guid` | Joins the `rack:{rackId}` group after verifying the rack exists. Throws a `HubException` if the rack does not exist (no group joined). |
| `UnsubscribeFromRack` | `rackId: guid` | Leaves the `rack:{rackId}` group. |

There is **no** method that mutates state or triggers discovery.

## Event envelope

Every event is a JSON object with a `type` discriminator, an `eventId`, and its fields:

```jsonc
// snapshot-updated
{
  "type": "snapshot-updated",
  "eventId": "0f9a...-uuid",
  "rackId": "…",
  "jobId": "…",              // optional (null if unknown)
  "snapshotId": "…",
  "version": 7,
  "summary": {
    "switchCount": 2, "serverCount": 20, "vlanCount": 3,
    "added": 4, "removed": 0, "modified": 1
  },
  "timestamp": "2026-07-28T04:00:00+00:00",
  "seq": 7,                  // equals version (monotonic per rack)
  "correlationId": "…"
}

// discovery-job-status-changed
{
  "type": "discovery-job-status-changed",
  "eventId": "…",
  "rackId": "…",
  "jobId": "…",
  "status": "InProgress",    // Queued | InProgress | Succeeded | Failed | Canceled
  "previousStatus": "Queued",
  "currentStep": null,
  "errorCode": null,         // an operator-safe code on failure (never a raw exception)
  "timestamp": "…",
  "seq": 3,                  // cluster-monotonic per jobId
  "correlationId": "…"
}

// heartbeat
{ "type": "heartbeat", "eventId": "…", "timestamp": "…" }
```

The summary is **counts only**. Events never contain the graph, host, port, MAC, credentialsRef or any
raw device data (NFR5).

## Intended client rules

A conforming client (e.g. the future Angular `TopologySignalRService`) should:

1. **Subscribe per rack.** On entering a rack view, call `SubscribeToRack(rackId)`; call
   `UnsubscribeFromRack(rackId)` on leave.
2. **Refetch on `snapshot-updated`.** Do not treat the summary as authoritative topology — on receipt
   for the currently-viewed rack, refetch `GET api/racks/{rackId}/topology/snapshots/latest` (and/or
   `GET api/discovery/jobs/{jobId}`) and clear the stale indicator.
3. **De-duplicate and never regress.** Ignore an event whose `(jobId, seq)` (job stream) or per-rack
   `seq` (snapshot stream) is not newer than the last one applied; `eventId` is an additional de-dup
   key. Pub/sub reconnection can duplicate or reorder — the UI must not move from newer to older state.
4. **Detect staleness.** If no heartbeat or event arrives for **30 seconds**, show the stale-data
   indicator (visible text, not colour-only; announced to screen readers — WCAG 2.1 AA) while still
   allowing read-only interaction with last-known data.
5. **Reconnect with backoff.** On disconnect, reconnect with exponential backoff **plus jitter**:
   1s → 2s → 4s → 8s, capped at 30s. On reconnect, clear the stale indicator and perform a one-time
   refresh to cover any missed events.
