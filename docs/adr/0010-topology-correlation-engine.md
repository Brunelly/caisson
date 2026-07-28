# 0010 — Topology correlation & inference engine

## Status
Accepted

## Context
Story #6 delivers Caisson M0's crown-jewel capability: inferring physical topology (which server NIC is
on which switch port, and that port's VLANs) from read-only discovery output. The inputs are the story-3
driver info records — `SwitchTopologySnapshot` (ports, LLDP neighbours, bridge/MAC-learning host tables,
VLANs) and `ServerNicSnapshot` (NIC MACs, BMC inventory). The engine must be pure and deterministic (NFR1
no I/O, NFR2 stable ordering/scores), explainable (NFR4 every outcome carries a reason code), fast at rack
scale (NFR3 <200ms), and vendor-agnostic (NFR5 POCOs only). Correctness *and ambiguity* are the whole
point: confident 1:1 mappings, ranked ambiguous candidates, and explicit unmapped NICs/ports.

The story answered three questions that this ADR fixes: confidence bands **High ≥ 0.8 / Medium 0.5–0.79 /
Low < 0.5**; LAG members (same MAC on multiple ports of one aggregation) are **ambiguous with a
`PortsInSameLag` reason and equal boosted confidence**; and MAC-learning entry age *should* be preferred —
but the story-3 `BridgeHostEntry` carries **no timestamp** (provenance deferred, ADR 0008), so recency
cannot be honoured here.

## Decision
- **A new pure `Caisson.Correlation` project**, layered exactly like `Caisson.Drivers.Abstractions`:
  `IsAotCompatible`, references only `Caisson.Domain` + `Caisson.Drivers.Abstractions` + the centrally
  pinned DI abstractions. No EF Core / Npgsql / `System.Net.Http` — enforced by a reflection guard test
  (NFR1). `AddTopologyCorrelation()` registers the stateless engine as a singleton.
- **A string-keyed result graph distinct from the Guid-keyed EF `TopologyCandidateMapping`.** The engine
  returns `TopologyCorrelationResult { Mappings, AmbiguousMappings, UnmappedNics, UnmappedPorts }` keyed by
  `ServerId` / `MacAddressValue` / (`SwitchId`, `PortName`). Folding this into the persisted, snapshot-scoped
  `TopologyCandidateMapping` is a **later persistence story's** job — and minting `Guid.NewGuid()` here would
  break NFR2 determinism anyway. The result shape is deliberately foldable into the EF entity later.
- **Reuse and extend the shared `Caisson.Domain.ReasonCode` enum** rather than forking a parallel
  correlation vocabulary — consistent with ADR 0006's `DriverDiagnostic` precedent. New members are
  **append-only** (existing ordinals unchanged) and each **≤ 32 chars**, because the enum persists as
  `HasConversion<string>().HasMaxLength(32)`. Added: `MacLearnUnique`, `LldpConsistent`, `LldpContradicts`,
  `MultipleMacPorts`, `PortsInSameLag`, `SeenOnTrunkPort`, `VlanInferred`, `VlanContextMissing`,
  `PortNeighbourUnknown`; existing `NotSeenInSwitch` / `NotSeenInBmc` / `MissingLldp` /
  `ConflictingMacEvidence` / `DuplicateMac` / `StaleData` / `ParseError` are reused as-is.
- **Rule-based additive scoring** (weights in `CorrelationScoring`, documented in
  [docs/topology-correlation.md](../topology-correlation.md)): access bridge hit `0.70` (+`MacLearnUnique`
  when the sole host), `+0.25` for consistent LLDP or `+0.15` for missing LLDP, VLANs from
  `{Pvid} ∪ TaggedVlans`. Trunk-only sightings get a flat `0.15` (`SeenOnTrunkPort`). Scores are
  `Math.Clamp`ed to `[0,1]` and rounded to 6 dp before `ConfidenceScore.From`. Bands are a
  presentation-layer helper (`ConfidenceBands`); the domain `ConfidenceScore` stays band-agnostic.
- **Access-vs-trunk classification** via combined signals, LLDP peer-switch first: a port is a trunk if an
  LLDP neighbour identifies *another switch in the snapshot*, **or** it tags more than one VLAN, **or** it
  has learned more than `TrunkMacCountThreshold` (4) distinct MACs. A MAC on both an access and a trunk
  port resolves to the access port (the trunk sighting is demoted).
- **LAG heuristic** (answered question): when all candidates share one switch and identical `(Pvid, sorted
  TaggedVlans)`, they are treated as one LAG — `PortsInSameLag` plus equal boosted Medium-band scores.
  Otherwise competing candidates are penalised and flagged `ConflictingMacEvidence`.
- **Deterministic ordering** (NFR2): the four collections are sorted by their natural keys (`ServerId`,
  `NicName` / `SwitchId`, `PortName`); ambiguous candidates by `(Confidence desc, SwitchId, PortName)`
  ordinal; scores rounded before ordering so ties break identically every run. No dependence on
  hash/dictionary enumeration order.

## Consequences
- The engine is unit-testable with synthetic POCOs (no drivers, no hardware) and reusable by future
  orchestration/persistence layers. Determinism is proven by a 20-run byte-identical serialization test and
  purity by a reflection guard; a rack-scale smoke test guards against accidental O(n²).
- **Staleness/recency scoring is not honoured** — `BridgeHostEntry` has no timestamp (ADR 0008). The
  `StaleData` reason stays in the vocabulary as an inert seam and is *not* weighted; honouring the answered
  question requires a future, scoped widening of the story-3 driver records (per the Key Area instruction
  not to widen those abstractions here). Follow-up.
- **LAG detection is a VLAN-config-shape heuristic, not real LACP membership** — two access ports with the
  same VLAN config that happen to learn the same MAC will read as a LAG. Genuine bond/LACP state would need
  richer driver evidence. Follow-up.
- **Idle ports (no MAC, no LLDP) and trunk/uplink ports are intentionally excluded from `UnmappedPorts`**
  as noise reduction; every port carrying a correlation-relevant signal is still reported.
- Extending correlation later (new evidence, new reason codes) is append-only against the shared enum and
  the `CorrelationScoring` table, with no change to the driver abstractions.
