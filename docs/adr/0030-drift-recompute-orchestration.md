# 0030 — Drift recompute orchestration: schedule, events, and retention

## Status

Accepted

## Context

Story #64 (AC4) requires drift to recompute on a schedule AND in reaction to two events (a new observed
snapshot, a new desired-state revision), with per-rack failure isolation, plus a retention policy
(NFR5/Q3: hybrid last-200-per-rack and max-180-days) that prunes without breaking references. The two
event sources live in different layers below `Caisson.Orchestration`: `TopologySnapshotIngestionService`
in `Caisson.Infrastructure`, and `DesiredStateIngestionService` in `Caisson.Ingestion`. Neither may
reference `Caisson.Orchestration` (it references them, not the reverse), so the event hooks cannot call
an Orchestration-owned coordination type directly.

## Decision

- **`IDriftRecomputeSignal` is defined in `Caisson.Infrastructure`**, mirroring
  `ITopologyEventPublisher`'s existing seam exactly: a dependency-free interface with a hard "never
  throws" contract, a `NoOpDriftRecomputeSignal` default (registered by
  `Caisson.Infrastructure.DependencyInjection.DriftServiceCollectionExtensions.AddCaissonDriftComputation`),
  and the real implementation, `Caisson.Orchestration.Drift.DriftRecomputeSignal` (a bounded, drop-newest-
  write `Channel<Guid>` copied from `DiscoveryJobSignal`), registered by
  `Caisson.Orchestration.DependencyInjection.DriftServiceCollectionExtensions.AddCaissonDrift` — overriding
  the no-op default via the same `RemoveAll` + re-register pattern `AddCaissonLiveUpdates` already uses.
  This keeps both event hooks calling only an Infrastructure-layer abstraction while the actual queue and
  its draining `DriftRecomputeRunner` stay in Orchestration.
- **Both event hooks are additive and best-effort.** `TopologySnapshotIngestionService` enqueues from
  inside `PublishSnapshotUpdatedAsync` — the same atomic choke point every snapshot persist already flows
  through — right after the (already fail-open) live-update publish; no try/catch is needed there because
  `IDriftRecomputeSignal.Enqueue` itself is contractually non-throwing. `DesiredStateIngestionService`
  hooks `ProcessFileAsync`'s success path (the same seam that writes the `desired-state.revision.ingested`
  audit event): it resolves `rackSlug → Rack.Id` (a rack with no aliased observed-state `Rack` row yet has
  nothing to compute drift against, so it is silently skipped) and enqueues, with the RACK-LOOKUP query
  itself wrapped in try/catch since a DB fault there is a real, if unlikely, failure mode that must never
  abort desired-state ingestion.
- **`DriftScheduler` and both event hooks funnel through the SAME queue and the SAME
  `IDriftComputationService.ComputeAndPersistAsync` entry point.** The scheduler does not compute drift
  itself; each tick enumerates racks with both an active desired revision
  (`LatestDesiredStateVersionQueries.LatestVersionPerRackAsync`, joined to `Rack` by
  `RackSlug == ExternalKey`, ADR 0029) and a latest observed snapshot, and enqueues each eligible rack onto
  the identical `DriftRecomputeSignal` the event hooks use. `DriftRecomputeRunner` (mirroring
  `DesiredStateIngestionRunner`, not the resumable-claim shape of `DiscoveryJobRunner` — one compute is a
  single bounded operation) drains the queue and performs the actual compute-and-persist. Each rack in the
  scheduler's per-tick enumeration is evaluated in its own try/catch (AC4 isolation); `DriftComputationService`
  itself already isolates engine/persistence failures per rack by recording a `Failed` report (ADR 0028)
  rather than throwing.
- **`DriftOrchestrationOptions` and `Caisson.Drift.DriftComputationOptions` share the `Drift:Computation`
  configuration section but are bound independently onto two separate POCOs.** `DriftComputationOptions`
  (pure, `Caisson.Drift`) owns `MaxItemsPerReport`, bound directly by
  `AddCaissonDriftComputation` in Infrastructure; `DriftOrchestrationOptions` (`Caisson.Orchestration`)
  owns `SchedulerEnabled`/`SchedulerPollSeconds`/`RetentionEnabled`/`RetentionPollSeconds`/
  `RetentionMaxReportsPerRack`/`RetentionMaxDays`. Splitting the option object was necessary — Orchestration
  cannot be referenced back from the pure engine's options type without inverting the layering the
  engine's own purity guard enforces — but the section name is the same constant string on both types, so
  operators configure drift behaviour from one `Drift:Computation` config block regardless of which
  property lives on which class.
- **`DriftRetentionPruner`** (same `PeriodicTimer`/`TimeProvider` shape as the scheduler) enforces the
  hybrid policy per rack: a report survives only if it is both among the rack's newest
  `RetentionMaxReportsPerRack` reports AND no older than `RetentionMaxDays`. Deleting a `DriftReport`
  cascades its `DriftItem` rows via the DB FK (ADR 0028's `Cascade` on `drift_item.drift_report_id`), so no
  separate item-level delete pass is needed. Loading one rack's ordered report id/timestamp list in full
  to decide what to prune is accepted as self-limiting by policy (once the pruner has run at least once,
  a rack's report count stays around the configured cap) rather than the kind of sitewide unbounded query
  the hardening invariant guards against.

## Consequences

- A future reader expecting `DriftRecomputeSignal` itself to be referenced from `Caisson.Ingestion`/
  `Caisson.Infrastructure` will not find it — only the `IDriftRecomputeSignal` interface is, by design
  (layering). Introducing a THIRD event source below Orchestration should reuse this same interface rather
  than growing a parallel notification path.
- Because the scheduler only enqueues (it never computes drift inline), a scheduler tick completing does
  NOT mean every eligible rack's drift has been recomputed yet — only that it has been queued for
  `DriftRecomputeRunner` to process. Tests asserting scheduler behaviour must drain the queue (or call
  `DriftRecomputeRunner.ProcessOneAsync` directly) to observe the persisted result.
- `RetentionMaxReportsPerRack`/`RetentionMaxDays` defaults (200 / 180) match the story's answered
  retention question; operators needing a different policy configure `Drift:Computation` directly, no
  code change required.
