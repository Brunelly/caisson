# 0064 — Guaranteed audit durability: three explicit tiers, transactional outbox, first-N denials

## Status

Accepted

## Context

Caisson needs a complete, tamper-evident audit trail for mutating and security-sensitive operations. The
shipped writer (ADR 0022) is fire-and-forget: `ChannelAuditEventWriter` enqueues onto a bounded
`Channel<AuditWriteRequest>` (capacity 4096, `FullMode = DropWrite`) drained off the request path by
`AuditEventBackgroundWriter`. That keeps the audit `INSERT` off the read path's latency budget, but makes
every record — including state mutations — best-effort and silently droppable, and lets a burst of
forbidden requests (ADR 0036) evict genuine records from the same bounded channel. Two prior attempts at
this story were stopped for spec defects (an implicit demand for bounded-AND-lossless-AND-stateless denial
counting, which is mathematically impossible, and ambiguous project placement for the mandatory-audit
seam). Forces:

- Story #308 AC1/AC2: a mandatory audit event must commit atomically with the mutation it records, and
  must never be silently dropped under load — at-least-once, de-duplicated by a stable id.
- Story #308 AC3/NFR2: an authorization-denial flood from one principal must retain a bounded, meaningful
  signal without losing another principal's (or Tier 1's) records.
- Story #308 AC4: audit persistence must stay off the read request path.
- Project reference graph: `Caisson.Orchestration`, `Caisson.Ingestion` and `Caisson.Infrastructure` all
  need to produce Tier 1 events (job terminal transitions, PR/ingestion/drift mutations), but none of them
  may reference `Caisson.Api` (verified from the `.csproj` graph — only `Caisson.Api` references
  `Caisson.Orchestration`/`Caisson.Ingestion`, never the reverse). `ICorrelationContext` lives in
  `Caisson.Api.Middleware`, so a shared seam cannot depend on it either.
- Multiple API instances always run; the outbox drain and the denial-bucket writes must be
  concurrency-safe across replicas with no double-dispatch and no lost/double-counted increments.
- Schema changes must be additive only, with indexes for the drain query and the bucket lookup, and must
  not touch `topology_audit_event`'s shape, `AuditQueries`, `AuditController` or `AuditEventDto`.

## Decision

**Three explicit durability tiers**, classified in code so a Tier 1/2 event can never reach the droppable
Tier 3 channel:

1. **Tier 1 — mandatory-durable** (state mutations: draft create/update, PR create/publish/close, drift
   apply, schedule change, discovery/drift-apply job terminal transitions — including transitions made by
   the stale/timeout reapers, which are state changes too). Written via `IMandatoryAuditOutbox.Add(context,
   envelope, occurredAtUtc)` — placed in `Caisson.Infrastructure` (not `Caisson.Api`) specifically so
   `Caisson.Orchestration`/`Caisson.Ingestion`/`Caisson.Infrastructure` can all resolve it. It only `Add`s
   an `AuditOutboxMessage` to the caller's own `CaissonDbContext`; it never calls `SaveChangesAsync` — the
   mutation owner keeps the single commit, so a mutation can never commit without its audit row and a
   rolled-back transaction can never leave an orphan one. **The outbox row's id IS the eventual
   `topology_audit_event.id`** — this is what makes redispatch after a crash or lease expiry idempotent
   (`ON CONFLICT (id) DO NOTHING`). A background `AuditOutboxDispatcher` (`Caisson.Api`, since
   `Caisson.Infrastructure` has no `Microsoft.Extensions.Hosting` reference) claims due rows with the
   codebase's established `FOR UPDATE SKIP LOCKED` lease pattern, projects each claimed batch into
   `topology_audit_event` and marks it `Dispatched` in ONE transaction, retries transient failures with
   backoff, and marks a row `Poisoned` (full payload retained, never deleted, a sanitized stable
   `failure_code` only) after `OutboxMaxAttempts`. A poisoned row is never marked `Dispatched` by any code
   path. Never dropped, never aggregated — safe from flooding because producing one requires real,
   already-rate-limited mutation work.
2. **Tier 2 — durable-first-N + bounded counter** (authorization denials and anything an unauthorized
   caller can trigger at will). For each `(actorId, endpoint, outcome, window)` bucket — keyed on the
   resolved stable actor id and the STABLE route template + HTTP method, never the raw path/query string,
   or bucket cardinality becomes attacker-controlled — the first `DenialFirstN` distinct denials are
   written durably and immediately: `INSERT INTO audit_denial_bucket ... ON CONFLICT (actor_id, endpoint,
   outcome, window_start_at_utc) DO NOTHING`, then lock the row and, while `durable_count < N`, insert the
   verbatim `authorization.forbidden` `TopologyAuditEvent` and increment `durable_count` in the SAME
   transaction. Concurrent cold requests across replicas serialize on this row, so the first-N guarantee is
   GLOBAL, not per-instance. Once N is reached, saturation for that bucket is cached in memory until the
   window expires, so the flood path performs no further bucket DB writes. Overflow beyond N is counted
   in-process by a bounded `DenialOverflowAccumulator` (principal, endpoint, outcome, first/last-seen,
   count) and flushed by `AuditDenialFlushService` on a short interval, on capacity pressure, and on
   graceful shutdown, as ONE durable aggregate `TopologyAuditEvent` per (bucket, flush-batch) using a
   stable batch UUID as the audit id (`ON CONFLICT (id) DO NOTHING`) so a retried flush cannot double-count.
   **ACCEPTED, DOCUMENTED LOSS:** an ungraceful crash may lose at most the current flush interval's OVERFLOW
   COUNT (seconds) — it can never lose a first-N verbatim record, and it can never affect Tier 1. This is
   deliberate: a denial counter is a security signal, not a financial ledger, and the alternative (bounded
   writes AND zero loss AND no in-memory state) is not achievable simultaneously.
3. **Tier 3 — best-effort** (high-volume read auditing). Unchanged from ADR 0022: the bounded
   `Channel<AuditWriteRequest>` (`DropWrite` on saturation) drained by `AuditEventBackgroundWriter`, now
   behind the explicit `IBestEffortAuditEventWriter` seam (`ChannelAuditEventWriter` renamed
   `BestEffortAuditEventWriter`) so it is reachable ONLY through a name that says "droppable" — no generic
   `IAuditEventWriter` remains through which a Tier 1/2 event could land here by accident.

**Schema** (additive, `snake_case`): `audit_outbox` (bounded audit columns as real columns — not an opaque
blob — plus `status`/`available_at_utc`/`attempt_count`/`lease_until_utc`/`claimed_by`/`dispatched_at_utc`/
`failure_code`; partial index on `(status, available_at_utc) WHERE status = 'Pending'`) and
`audit_denial_bucket` (unique index on `(actor_id, endpoint, outcome, window_start_at_utc)` — the bucket
lookup — plus an index on `window_end_at_utc` for expiry sweeps). Neither table touches
`topology_audit_event`, `AuditQueries`, `AuditController` or `AuditEventDto`.

**Supersession:** this ADR supersedes the audit-durability portions of **ADR 0022** (which accepted
eventually-consistent, droppable audit writes for ALL events, including mutations) and **ADR 0036** (which
labelled the `authorization.forbidden` audit write best-effort/additive with no durability guarantee) —
specifically the claims that a mutation's audit record may be lost and that a denial flood may evict other
callers' records. ADR 0022's rate-limiting decision and ADR 0036's policy-matching/body-peek mechanics are
NOT reopened; only their audit-durability characterization is replaced.

**Out of scope** (explicitly, not attempted here): per-rack/tenant isolation (ADR 0023, owned by the T4
Multi-tenancy epic); story #172's Key Vault credential-provider selection; SOC2 evidence-export format.
This story has **no UI surface** — the supplied design-system token JSON and the three theme overview HTML
files were reviewed and introduce no page, component, or visual state for this work item.

## Consequences

- Every mutation call site now does exactly one `IMandatoryAuditOutbox.Add(...)` before its own
  `SaveChangesAsync`, and no longer performs a separate post-save best-effort audit write for the same
  event — eliminating the double-audit some call sites previously had.
- The dispatcher adds at-least-once latency (bounded by `OutboxPollIntervalSeconds` and lease expiry) before
  a Tier 1 event is queryable via the existing `AuditQueries`/`AuditController` surface — an accepted
  trade-off for the durability guarantee; the write itself is synchronously durable at mutation-commit time
  even though its projection into `topology_audit_event` is asynchronous.
- A permanently failing dispatch (e.g. a details payload that would violate a `topology_audit_event`
  constraint) surfaces as a `Poisoned` row rather than blocking the batch or silently vanishing; this is a
  new operator-facing failure mode requiring triage (surfaced via `AuditOutboxHealthCheck`, Degraded-only —
  a backlog or poisoned rows must never fail `/health/ready`).
- Tier 2's bucket-row locking adds one extra durable write (or a locked existing-row read) to the first N
  forbidden requests per bucket per window; beyond N, the request path only touches an in-memory
  dictionary — bounded by `(distinct buckets × windows)`, not by request volume, so a flood cannot cause
  unbounded writes and cannot evict Tier 1 or another principal's records.
- The Tier 2 overflow-count loss window is a deliberate, documented trade — reviewers must not reopen it as
  a defect; the first-N verbatim records and everything in Tier 1 remain loss-free across an ungraceful
  restart.
- `IAuditEventWriter` (the pre-tier interface) is retired only once every call site is reclassified onto
  Tier 1, `IAuthorizationDenialAuditWriter`, or `IBestEffortAuditEventWriter`; a source-level architecture
  test enforces that no mutation path depends on the best-effort writer and that the literal
  `authorization.forbidden` action string appears nowhere outside the Tier 2 implementation and its tests.
